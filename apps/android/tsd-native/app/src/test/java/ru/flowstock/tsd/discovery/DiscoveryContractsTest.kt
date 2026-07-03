package ru.flowstock.tsd.discovery

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.json.JSONObject

class DiscoveryContractsTest {
    @Test
    fun udpRequestContainsProductProtocolAndNonce() {
        val request = String(buildUdpDiscoveryRequest("0123456789abcdef0123456789abcdef"), Charsets.UTF_8)
        val json = JSONObject(request)

        assertEquals("FlowStock", json.getString("product"))
        assertEquals(1, json.getInt("discovery_protocol_version"))
        assertEquals("0123456789abcdef0123456789abcdef", json.getString("nonce"))
        assertTrue(request.toByteArray(Charsets.UTF_8).size <= DISCOVERY_MAX_UDP_PACKET_BYTES)
    }

    @Test
    fun parsesValidUdpResponseAndNormalizesCanonicalRoot() {
        val hint = parseUdpDiscoveryResponse(
            """{"product":"FlowStock","discovery_protocol_version":1,"nonce":"n","instance_name":"A","canonical_https_base_url":"https://FLOWSTOCK.local:7154/","application_version":"70"}""",
            "0123456789abcdef0123456789abcdef",
        )

        assertNull(hint)

        val valid = parseUdpDiscoveryResponse(
            """{"product":"FlowStock","discovery_protocol_version":1,"nonce":"0123456789abcdef0123456789abcdef","instance_name":"A","canonical_https_base_url":"https://FLOWSTOCK.local:7154/","application_version":"70"}""",
            "0123456789abcdef0123456789abcdef",
        )

        assertNotNull(valid)
        assertEquals("https://flowstock.local:7154", valid!!.canonicalRootUrl)
        assertEquals("A", valid.instanceName)
        assertEquals("70", valid.applicationVersion)
    }

    @Test
    fun ignoresInvalidUdpResponseIdentity() {
        val nonce = "0123456789abcdef0123456789abcdef"
        assertNull(parseUdpDiscoveryResponse("""{"product":"Other","discovery_protocol_version":1,"nonce":"0123456789abcdef0123456789abcdef"}""", nonce))
        assertNull(parseUdpDiscoveryResponse("""{"product":"FlowStock","discovery_protocol_version":2,"nonce":"0123456789abcdef0123456789abcdef"}""", nonce))
        assertNull(parseUdpDiscoveryResponse("""{"product":"FlowStock","discovery_protocol_version":1,"nonce":"fedcba9876543210fedcba9876543210"}""", nonce))
        assertNull(parseUdpDiscoveryResponse("""not-json""", nonce))
    }

    @Test(expected = IllegalArgumentException::class)
    fun udpRequestRejectsInvalidNonce() {
        buildUdpDiscoveryRequest("short")
    }
}
