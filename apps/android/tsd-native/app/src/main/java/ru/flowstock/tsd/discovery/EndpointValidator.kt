package ru.flowstock.tsd.discovery

import org.json.JSONObject
import ru.flowstock.tsd.config.ServerEndpoint
import ru.flowstock.tsd.config.ServerEndpointNormalizer
import java.io.BufferedInputStream
import java.net.HttpURLConnection
import java.net.URL
import javax.net.ssl.HttpsURLConnection

interface HttpGetter {
    fun get(url: String, maxBodyBytes: Int): HttpResponse
    fun getPrefix(url: String, maxBodyBytes: Int): HttpResponse
    fun head(url: String): HttpResponse
}

data class HttpResponse(
    val statusCode: Int,
    val body: String,
    val contentType: String? = null,
)

class HttpsUrlConnectionGetter(
    private val connectTimeoutMs: Int = 3_000,
    private val readTimeoutMs: Int = 3_000,
) : HttpGetter {
    override fun get(url: String, maxBodyBytes: Int): HttpResponse {
        val connection = openHttpsConnection(url)
        connection.requestMethod = "GET"
        return try {
            val status = connection.responseCode
            val stream = if (status in 200..399) connection.inputStream else connection.errorStream
            val body = stream?.use { readLimitedUtf8(it, maxBodyBytes) }.orEmpty()
            HttpResponse(status, body, connection.contentType)
        } finally {
            connection.disconnect()
        }
    }

    override fun head(url: String): HttpResponse {
        val connection = openHttpsConnection(url)
        connection.requestMethod = "HEAD"
        return try {
            HttpResponse(connection.responseCode, "", connection.contentType)
        } finally {
            connection.disconnect()
        }
    }

    override fun getPrefix(url: String, maxBodyBytes: Int): HttpResponse {
        val connection = openHttpsConnection(url)
        connection.requestMethod = "GET"
        return try {
            val status = connection.responseCode
            val stream = if (status in 200..399) connection.inputStream else connection.errorStream
            val body = stream?.use { readUtf8Prefix(it, maxBodyBytes) }.orEmpty()
            HttpResponse(status, body, connection.contentType)
        } finally {
            connection.disconnect()
        }
    }

    private fun openHttpsConnection(url: String): HttpsURLConnection {
        val connection = URL(url).openConnection()
        if (connection !is HttpsURLConnection) {
            throw IllegalArgumentException("Only HTTPS endpoints are allowed")
        }
        connection.connectTimeout = connectTimeoutMs
        connection.readTimeout = readTimeoutMs
        connection.instanceFollowRedirects = false
        return connection
    }

    private fun readLimitedUtf8(stream: java.io.InputStream, maxBodyBytes: Int): String {
        if (maxBodyBytes <= 0) return ""
        val input = BufferedInputStream(stream)
        val output = java.io.ByteArrayOutputStream()
        val buffer = ByteArray(4096)
        var total = 0
        while (true) {
            val read = input.read(buffer)
            if (read < 0) break
            total += read
            if (total > maxBodyBytes) {
                throw IllegalStateException("HTTP response body exceeds limit")
            }
            output.write(buffer, 0, read)
        }
        return output.toString(Charsets.UTF_8.name())
    }

    private fun readUtf8Prefix(stream: java.io.InputStream, maxBodyBytes: Int): String {
        if (maxBodyBytes <= 0) return ""
        val input = BufferedInputStream(stream)
        val output = java.io.ByteArrayOutputStream()
        val buffer = ByteArray(4096)
        var remaining = maxBodyBytes
        while (remaining > 0) {
            val read = input.read(buffer, 0, minOf(buffer.size, remaining))
            if (read < 0) break
            output.write(buffer, 0, read)
            remaining -= read
        }
        return output.toString(Charsets.UTF_8.name())
    }
}

class EndpointValidator(
    private val httpGetter: HttpGetter = HttpsUrlConnectionGetter(),
    private val clock: () -> Long = { System.currentTimeMillis() },
) {
    fun validate(input: String): EndpointValidationResult {
        val root = ServerEndpointNormalizer.normalizeRootUrl(input)
            ?: return EndpointValidationResult.Failure("invalid-root-url")

        val discovery = runCatching { httpGetter.get("$root/api/discovery", JSON_BODY_LIMIT_BYTES) }
            .getOrElse { return EndpointValidationResult.Failure("discovery-request-failed") }
        if (discovery.statusCode !in 200..299) {
            return EndpointValidationResult.Failure("discovery-status-${discovery.statusCode}")
        }

        val discoveryJson = runCatching { JSONObject(discovery.body) }
            .getOrElse { return EndpointValidationResult.Failure("discovery-json-invalid") }
        if (discoveryJson.optString("product") != DISCOVERY_PRODUCT
            || discoveryJson.optInt("discovery_protocol_version") != DISCOVERY_PROTOCOL_VERSION) {
            return EndpointValidationResult.Failure("discovery-identity-mismatch")
        }
        val responseRoot = ServerEndpointNormalizer.normalizeRootUrl(
            discoveryJson.optString("canonical_https_base_url"),
        )
        if (responseRoot != root) {
            return EndpointValidationResult.Failure("discovery-canonical-mismatch")
        }

        val ping = runCatching { httpGetter.get("$root/api/ping", JSON_BODY_LIMIT_BYTES) }
            .getOrElse { return EndpointValidationResult.Failure("ping-request-failed") }
        if (ping.statusCode !in 200..299) {
            return EndpointValidationResult.Failure("ping-status-${ping.statusCode}")
        }
        val pingJson = runCatching { JSONObject(ping.body) }
            .getOrElse { return EndpointValidationResult.Failure("ping-json-invalid") }
        if (pingJson.optBoolean("ok") != true) {
            return EndpointValidationResult.Failure("ping-not-ok")
        }

        val tsd = runCatching { httpGetter.head("$root/tsd/") }
            .getOrElse { return EndpointValidationResult.Failure("tsd-request-failed") }
        if (tsd.statusCode !in 200..299) {
            return EndpointValidationResult.Failure("tsd-status-${tsd.statusCode}")
        }
        val contentType = tsd.contentType.orEmpty().lowercase()
        if (!contentType.contains("text/html")) {
            val prefix = runCatching { httpGetter.getPrefix("$root/tsd/", TSD_PREFIX_LIMIT_BYTES) }
                .getOrElse { return EndpointValidationResult.Failure("tsd-shell-marker-missing") }
            if (prefix.statusCode !in 200..299) {
                return EndpointValidationResult.Failure("tsd-status-${prefix.statusCode}")
            }
            val bodyPrefix = prefix.body.lowercase()
            if (!bodyPrefix.contains("<!doctype html") && !bodyPrefix.contains("<html")) {
                return EndpointValidationResult.Failure("tsd-shell-marker-missing")
            }
        }

        return EndpointValidationResult.Success(
            ServerEndpoint(
                rootUrl = root,
                instanceName = discoveryJson.optString("instance_name").trim(),
                applicationVersion = discoveryJson.optString("application_version").trim(),
                validatedAt = clock(),
            ),
        )
    }

    companion object {
        const val JSON_BODY_LIMIT_BYTES = 64 * 1024
        const val TSD_PREFIX_LIMIT_BYTES = 1024
    }
}
