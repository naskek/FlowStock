package ru.flowstock.tsd.discovery

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.security.SecureRandom
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

data class Ipv4Network(
    val address: Inet4Address,
    val prefixLength: Int,
) {
    fun directedBroadcast(): InetAddress? {
        if (prefixLength !in 1..30) return null
        val addressInt = address.address.fold(0) { acc, byte -> (acc shl 8) or (byte.toInt() and 0xff) }
        val mask = -1 shl (32 - prefixLength)
        val broadcast = addressInt or mask.inv()
        val bytes = byteArrayOf(
            (broadcast ushr 24).toByte(),
            (broadcast ushr 16).toByte(),
            (broadcast ushr 8).toByte(),
            broadcast.toByte(),
        )
        return InetAddress.getByAddress(bytes)
    }
}

class ActiveLanNetworkProvider(private val context: Context) {
    fun activeNetwork(): SelectedLanNetwork? {
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val network = connectivity.activeNetwork ?: return null
        val capabilities = connectivity.getNetworkCapabilities(network) ?: return null
        val allowed = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
        val excluded = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) ||
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        if (!allowed || excluded) return null
        val linkProperties = connectivity.getLinkProperties(network) ?: return null
        val ipv4 = linkProperties.linkAddresses.firstNotNullOfOrNull { linkAddress ->
            val address = linkAddress.address
            if (address is Inet4Address) Ipv4Network(address, linkAddress.prefixLength) else null
        } ?: return null
        return SelectedLanNetwork(ipv4, AndroidNetworkSocketBinder(network))
    }
}

data class SelectedLanNetwork(
    val ipv4Network: Ipv4Network,
    val socketBinder: DiscoverySocketBinder,
)

interface DiscoverySocketBinder {
    fun bind(socket: DatagramSocket)
}

class AndroidNetworkSocketBinder(private val network: Network) : DiscoverySocketBinder {
    override fun bind(socket: DatagramSocket) {
        network.bindSocket(socket)
    }
}

class UdpServerDiscoveryClient(
    private val networkProvider: ActiveLanNetworkProvider,
    private val validator: EndpointValidator,
    private val random: SecureRandom = SecureRandom(),
) {
    fun discover(cancelled: AtomicBoolean = AtomicBoolean(false)): List<ServerDiscoveryResult> {
        val selectedNetwork = networkProvider.activeNetwork() ?: return emptyList()
        val broadcast = selectedNetwork.ipv4Network.directedBroadcast() ?: return emptyList()
        val nonce = randomNonce()
        val request = buildUdpDiscoveryRequest(nonce)
        if (request.size > DISCOVERY_MAX_UDP_PACKET_BYTES) return emptyList()

        val hints = linkedMapOf<String, DiscoveryHint>()
        val deadline = System.currentTimeMillis() + TOTAL_TIMEOUT_MS
        DatagramSocket().use { socket ->
            selectedNetwork.socketBinder.bind(socket)
            socket.broadcast = true
            socket.soTimeout = RECEIVE_TIMEOUT_MS
            repeat(ATTEMPTS) {
                if (cancelled.get() || System.currentTimeMillis() >= deadline) return@repeat
                socket.send(DatagramPacket(request, request.size, broadcast, DISCOVERY_UDP_PORT))
                while (!cancelled.get() && System.currentTimeMillis() < deadline) {
                    val buffer = ByteArray(DISCOVERY_MAX_UDP_PACKET_BYTES)
                    val packet = DatagramPacket(buffer, buffer.size)
                    val received = runCatching {
                        socket.receive(packet)
                        packet
                    }.getOrNull() ?: break
                    val text = String(received.data, received.offset, received.length, Charsets.UTF_8)
                    val hint = parseUdpDiscoveryResponse(text, nonce) ?: continue
                    hints.putIfAbsent(hint.canonicalRootUrl, hint)
                    if (hints.size >= MAX_CANDIDATES) {
                        return@repeat
                    }
                }
            }
        }

        val executor = Executors.newFixedThreadPool(MAX_VALIDATION_CONCURRENCY)
        return try {
            val tasks = hints.values.take(MAX_CANDIDATES).map { hint ->
                java.util.concurrent.Callable<ServerDiscoveryResult?> {
                    if (cancelled.get() || System.currentTimeMillis() >= deadline) {
                        return@Callable null
                    }
                    when (val validation = validator.validate(hint.canonicalRootUrl)) {
                        is EndpointValidationResult.Success -> ServerDiscoveryResult.Validated(validation.endpoint)
                        is EndpointValidationResult.Failure -> {
                            ServerDiscoveryResult.Rejected(hint.canonicalRootUrl, validation.reason)
                        }
                    }
                }
            }
            val remaining = (deadline - System.currentTimeMillis()).coerceAtLeast(0L)
            if (remaining <= 0L || cancelled.get()) {
                emptyList()
            } else {
                executor.invokeAll(tasks, remaining, TimeUnit.MILLISECONDS)
                    .mapNotNull { future -> runCatching { future.get() }.getOrNull() }
                    .filterNotNull()
            }
        } finally {
            executor.shutdownNow()
        }
    }

    private fun randomNonce(): String {
        val bytes = ByteArray(16)
        random.nextBytes(bytes)
        return bytes.joinToString("") { "%02x".format(it.toInt() and 0xff) }
    }

    companion object {
        const val TOTAL_TIMEOUT_MS = 10_000L
        const val MAX_CANDIDATES = 16
        const val MAX_VALIDATION_CONCURRENCY = 4
        private const val RECEIVE_TIMEOUT_MS = 1_000
        private const val ATTEMPTS = 3
    }
}

sealed class ServerDiscoveryResult {
    data class Validated(val endpoint: ru.flowstock.tsd.config.ServerEndpoint) : ServerDiscoveryResult()
    data class Rejected(val rootUrl: String, val reason: String) : ServerDiscoveryResult()
}
