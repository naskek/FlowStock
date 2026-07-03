package ru.flowstock.tsd.config

import java.net.URI

data class ServerEndpoint(
    val rootUrl: String,
    val instanceName: String = "",
    val applicationVersion: String = "",
    val validatedAt: Long = 0L,
) {
    fun discoveryUrl(): String = "$rootUrl/api/discovery"
    fun pingUrl(): String = "$rootUrl/api/ping"
    fun tsdUrl(): String = "$rootUrl/tsd/"
}

object ServerEndpointNormalizer {
    fun normalizeRootUrl(input: String?): String? {
        val text = input?.trim()?.trimEnd('/') ?: return null
        if (text.isBlank()) return null
        val uri = runCatching { URI(text) }.getOrNull() ?: return null
        val scheme = uri.scheme?.lowercase() ?: return null
        val host = uri.host ?: return null
        if (scheme != "https" || host.isBlank()) return null
        if (!uri.rawQuery.isNullOrBlank() || !uri.rawFragment.isNullOrBlank()) return null
        val path = uri.rawPath.orEmpty()
        if (path.isNotBlank() && path != "/") return null
        val port = if (uri.port >= 0) ":${uri.port}" else ""
        return "https://${host.lowercase()}$port"
    }

    fun sameRoot(left: String?, right: String?): Boolean =
        normalizeRootUrl(left) == normalizeRootUrl(right)
}
