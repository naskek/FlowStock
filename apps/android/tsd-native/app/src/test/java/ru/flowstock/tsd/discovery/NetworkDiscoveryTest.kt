package ru.flowstock.tsd.discovery

import java.net.InetAddress
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NetworkDiscoveryTest {
    @Test
    fun computesDirectedBroadcastFromIpv4Prefix() {
        val network = Ipv4Network(
            InetAddress.getByName("192.168.1.51") as java.net.Inet4Address,
            24,
        )

        assertEquals("192.168.1.255", network.directedBroadcast()?.hostAddress)
    }

    @Test
    fun rejectsUnsafeBroadPrefixesForDiscovery() {
        assertNull(
            Ipv4Network(
                InetAddress.getByName("10.0.0.5") as java.net.Inet4Address,
                0,
            ).directedBroadcast(),
        )
    }

    @Test
    fun discoveryConstantsBoundCandidatesAndConcurrency() {
        assertEquals(16, UdpServerDiscoveryClient.MAX_CANDIDATES)
        assertEquals(4, UdpServerDiscoveryClient.MAX_VALIDATION_CONCURRENCY)
        assertEquals(10_000L, UdpServerDiscoveryClient.TOTAL_TIMEOUT_MS)
    }

    @Test
    fun selectedNetworkHasSocketBindingAbstraction() {
        var bound = false
        val selection = SelectedLanNetwork(
            Ipv4Network(InetAddress.getByName("192.168.1.51") as java.net.Inet4Address, 24),
            object : DiscoverySocketBinder {
                override fun bind(socket: java.net.DatagramSocket) {
                    bound = true
                }
            },
        )

        selection.socketBinder.bind(java.net.DatagramSocket())

        assertTrue(bound)
        assertFalse(selection.ipv4Network.directedBroadcast() == null)
    }
}
