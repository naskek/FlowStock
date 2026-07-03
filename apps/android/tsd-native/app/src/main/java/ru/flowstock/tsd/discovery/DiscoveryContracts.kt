package ru.flowstock.tsd.discovery

import org.json.JSONObject
import ru.flowstock.tsd.config.ServerEndpoint
import ru.flowstock.tsd.config.ServerEndpointNormalizer

const val DISCOVERY_PRODUCT = "FlowStock"
const val DISCOVERY_PROTOCOL_VERSION = 1
const val DISCOVERY_UDP_PORT = 7155
const val DISCOVERY_MAX_UDP_PACKET_BYTES = 1024
const val DISCOVERY_NONCE_HEX_LENGTH = 32
private val DiscoveryNonceRegex = Regex("^[0-9a-f]{32}$")

data class DiscoveryHint(
    val instanceName: String,
    val canonicalRootUrl: String,
    val applicationVersion: String,
)

sealed class EndpointValidationResult {
    data class Success(val endpoint: ServerEndpoint) : EndpointValidationResult()
    data class Failure(val reason: String) : EndpointValidationResult()
}

fun parseUdpDiscoveryResponse(jsonText: String, expectedNonce: String): DiscoveryHint? {
    if (!isValidDiscoveryNonce(expectedNonce)) return null
    val json = runCatching { JSONObject(jsonText) }.getOrNull() ?: return null
    if (json.optString("product") != DISCOVERY_PRODUCT) return null
    if (json.optInt("discovery_protocol_version") != DISCOVERY_PROTOCOL_VERSION) return null
    if (json.optString("nonce") != expectedNonce) return null
    val rootUrl = ServerEndpointNormalizer.normalizeRootUrl(json.optString("canonical_https_base_url"))
        ?: return null
    return DiscoveryHint(
        instanceName = json.optString("instance_name").trim(),
        canonicalRootUrl = rootUrl,
        applicationVersion = json.optString("application_version").trim(),
    )
}

fun buildUdpDiscoveryRequest(nonce: String): ByteArray {
    require(isValidDiscoveryNonce(nonce)) { "Discovery nonce must be 32 lowercase hex characters" }
    val json = JSONObject()
        .put("product", DISCOVERY_PRODUCT)
        .put("discovery_protocol_version", DISCOVERY_PROTOCOL_VERSION)
        .put("nonce", nonce)
    return json.toString().toByteArray(Charsets.UTF_8)
}

fun isValidDiscoveryNonce(value: String?): Boolean =
    value != null && DiscoveryNonceRegex.matches(value)
