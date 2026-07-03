package ru.flowstock.tsd.discovery

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class EndpointValidatorTest {
    @Test
    fun validatesDiscoveryPingAndTsdInOrder() {
        val getter = FakeGetter(
            "https://flowstock.local:7154/api/discovery" to HttpResponse(
                200,
                """{"product":"FlowStock","discovery_protocol_version":1,"instance_name":"Main","canonical_https_base_url":"https://flowstock.local:7154","application_version":"70"}""",
            ),
            "https://flowstock.local:7154/api/ping" to HttpResponse(200, """{"ok":true}"""),
            "https://flowstock.local:7154/tsd/" to HttpResponse(200, "", "text/html; charset=utf-8"),
        )

        val result = EndpointValidator(getter, clock = { 123L }).validate("https://flowstock.local:7154")

        assertTrue(result is EndpointValidationResult.Success)
        val endpoint = (result as EndpointValidationResult.Success).endpoint
        assertEquals("https://flowstock.local:7154", endpoint.rootUrl)
        assertEquals("Main", endpoint.instanceName)
        assertEquals("70", endpoint.applicationVersion)
        assertEquals(123L, endpoint.validatedAt)
        assertEquals(
            listOf(
                "https://flowstock.local:7154/api/discovery:${EndpointValidator.JSON_BODY_LIMIT_BYTES}",
                "https://flowstock.local:7154/api/ping:${EndpointValidator.JSON_BODY_LIMIT_BYTES}",
                "HEAD https://flowstock.local:7154/tsd/",
            ),
            getter.calls,
        )
    }

    @Test
    fun tsdAvailabilityDoesNotReadLargeShellBody() {
        val getter = FakeGetter(
            "https://flowstock.local:7154/api/discovery" to HttpResponse(
                200,
                """{"product":"FlowStock","discovery_protocol_version":1,"instance_name":"Main","canonical_https_base_url":"https://flowstock.local:7154","application_version":"70"}""",
            ),
            "https://flowstock.local:7154/api/ping" to HttpResponse(200, """{"ok":true}"""),
            headResponses = mapOf(
                "https://flowstock.local:7154/tsd/" to HttpResponse(
                    statusCode = 200,
                    body = "",
                    contentType = "application/octet-stream",
                ),
            ),
            prefixResponses = mapOf(
                "https://flowstock.local:7154/tsd/" to HttpResponse(
                    statusCode = 200,
                    body = "<!doctype html>" + "x".repeat(EndpointValidator.TSD_PREFIX_LIMIT_BYTES),
                    contentType = "application/octet-stream",
                ),
            ),
            failIfTsdGetIsUsed = true,
        )

        val result = EndpointValidator(getter).validate("https://flowstock.local:7154")

        assertTrue(result is EndpointValidationResult.Success)
        assertTrue(getter.calls.contains("HEAD https://flowstock.local:7154/tsd/"))
        assertTrue(getter.calls.contains("PREFIX https://flowstock.local:7154/tsd/:${EndpointValidator.TSD_PREFIX_LIMIT_BYTES}"))
    }

    @Test
    fun rejectsHostnameOnlyCertificateFlowThroughIpCandidateMismatch() {
        val getter = FakeGetter(
            "https://192.168.1.51:7154/api/discovery" to HttpResponse(
                200,
                """{"product":"FlowStock","discovery_protocol_version":1,"instance_name":"Main","canonical_https_base_url":"https://flowstock.local:7154","application_version":"70"}""",
            ),
        )

        val result = EndpointValidator(getter).validate("https://192.168.1.51:7154")

        assertTrue(result is EndpointValidationResult.Failure)
        assertEquals("discovery-canonical-mismatch", (result as EndpointValidationResult.Failure).reason)
    }

    @Test
    fun failedNewEndpointCanBeDetectedWithoutStoreMutation() {
        val result = EndpointValidator(FakeGetter()).validate("https://flowstock.local:7154/tsd/")

        assertTrue(result is EndpointValidationResult.Failure)
        assertEquals("invalid-root-url", (result as EndpointValidationResult.Failure).reason)
    }

    @Test
    fun pingNotOkRejectsEndpoint() {
        val getter = FakeGetter(
            "https://flowstock.local:7154/api/discovery" to HttpResponse(
                200,
                """{"product":"FlowStock","discovery_protocol_version":1,"instance_name":"Main","canonical_https_base_url":"https://flowstock.local:7154","application_version":"70"}""",
            ),
            "https://flowstock.local:7154/api/ping" to HttpResponse(200, """{"ok":false}"""),
        )

        val result = EndpointValidator(getter).validate("https://flowstock.local:7154")

        assertTrue(result is EndpointValidationResult.Failure)
        assertEquals("ping-not-ok", (result as EndpointValidationResult.Failure).reason)
    }

    private class FakeGetter(
        vararg responses: Pair<String, HttpResponse>,
        private val headResponses: Map<String, HttpResponse> = responses.toMap(),
        private val prefixResponses: Map<String, HttpResponse> = responses.toMap(),
        private val failIfTsdGetIsUsed: Boolean = false,
    ) : HttpGetter {
        private val responsesByUrl = responses.toMap()
        val calls = mutableListOf<String>()

        override fun get(url: String, maxBodyBytes: Int): HttpResponse {
            calls += "$url:$maxBodyBytes"
            if (failIfTsdGetIsUsed && url.endsWith("/tsd/")) {
                throw IllegalStateException("TSD GET should not be used for shell availability")
            }
            return responsesByUrl[url] ?: throw IllegalStateException("Missing response")
        }

        override fun head(url: String): HttpResponse {
            calls += "HEAD $url"
            return headResponses[url] ?: responsesByUrl[url] ?: throw IllegalStateException("Missing response")
        }

        override fun getPrefix(url: String, maxBodyBytes: Int): HttpResponse {
            calls += "PREFIX $url:$maxBodyBytes"
            return prefixResponses[url] ?: responsesByUrl[url] ?: throw IllegalStateException("Missing response")
        }
    }
}
