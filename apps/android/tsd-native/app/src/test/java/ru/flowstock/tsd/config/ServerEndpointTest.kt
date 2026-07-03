package ru.flowstock.tsd.config

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerEndpointTest {
    @Test
    fun normalizesOnlyHttpsRootUrl() {
        assertEquals(
            "https://flowstock.local:7154",
            ServerEndpointNormalizer.normalizeRootUrl(" https://FlowStock.Local:7154/ "),
        )
        assertNull(ServerEndpointNormalizer.normalizeRootUrl("http://flowstock.local:7154"))
        assertNull(ServerEndpointNormalizer.normalizeRootUrl("https://flowstock.local:7154/tsd/"))
        assertNull(ServerEndpointNormalizer.normalizeRootUrl("https://flowstock.local:7154?x=1"))
        assertNull(ServerEndpointNormalizer.normalizeRootUrl("https://flowstock.local:7154#x"))
    }

    @Test
    fun derivesPathsFromSingleRootUrl() {
        val endpoint = ServerEndpoint("https://flowstock.local:7154")

        assertEquals("https://flowstock.local:7154/api/discovery", endpoint.discoveryUrl())
        assertEquals("https://flowstock.local:7154/api/ping", endpoint.pingUrl())
        assertEquals("https://flowstock.local:7154/tsd/", endpoint.tsdUrl())
    }

    @Test
    fun comparesNormalizedRoots() {
        assertTrue(ServerEndpointNormalizer.sameRoot("https://FLOWSTOCK.local:7154/", "https://flowstock.local:7154"))
        assertFalse(ServerEndpointNormalizer.sameRoot("https://flowstock.local:7154", "https://other.local:7154"))
    }
}
