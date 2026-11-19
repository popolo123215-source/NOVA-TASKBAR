package com.nova.connect

import android.content.Context
import android.net.wifi.WifiManager
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.io.IOException
import java.net.MulticastSocket
import java.net.NetworkInterface
import java.net.SocketException
import java.util.concurrent.atomic.AtomicBoolean

class LanDiscovery(
    context: Context,
    private val identity: NovaIdentity,
    private val tcpPort: Int,
    private val peers: PeerStore,
    private val onPeersChanged: () -> Unit
) : AutoCloseable {
    private val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    private val multicastLock = wifiManager.createMulticastLock("NOVA-Connect-discovery").apply {
        setReferenceCounted(false)
    }
    private val running = AtomicBoolean(false)
    private var socket: MulticastSocket? = null
    private var thread: Thread? = null
    private var wifiInterface: NetworkInterface? = null
    @Volatile
    var error: String? = null
        private set

    @Synchronized
    fun start() {
        if (!running.compareAndSet(false, true)) return
        try {
            multicastLock.acquire()
            val networkInterface = findMulticastInterface()
            wifiInterface = networkInterface
            val multicastSocket = MulticastSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(DISCOVERY_PORT))
                networkInterface?.let { this.networkInterface = it }
                soTimeout = 1_000
                timeToLive = 1
                joinGroup(InetSocketAddress(MULTICAST_ADDRESS, DISCOVERY_PORT), networkInterface)
            }
            socket = multicastSocket
            thread = Thread({ runLoop(multicastSocket) }, "NOVA-LAN-discovery").apply {
                isDaemon = true
                start()
            }
        } catch (exception: Exception) {
            error = exception.message ?: exception.javaClass.simpleName
            close()
        }
    }

    @Synchronized
    fun refresh() {
        close()
        error = null
        start()
        error?.let { throw IOException("Could not refresh LAN discovery: $it") }
    }

    fun snapshot(): List<NovaPeer> {
        peers.clearExpired()
        return peers.allPeers()
    }

    fun addManualPeer(peer: NovaPeer) {
        peers.observe(peer)
        onPeersChanged()
    }

    fun trust(peer: NovaPeer) {
        peers.trust(peer)
        onPeersChanged()
    }

    fun trustedByFingerprint(fingerprint: String): NovaPeer? = peers.trustedByFingerprint(fingerprint)
    fun trustedByDeviceId(deviceId: String): NovaPeer? = peers.trustedByDeviceId(deviceId)

    private fun runLoop(multicastSocket: MulticastSocket) {
        var nextAnnouncementAt = 0L
        val buffer = ByteArray(MAX_ANNOUNCEMENT_BYTES)
        while (running.get() && !multicastSocket.isClosed) {
            val now = System.currentTimeMillis()
            if (now >= nextAnnouncementAt) {
                announcementPackets().forEach { packet ->
                    runCatching { multicastSocket.send(packet) }
                        .onFailure { error = it.message ?: it.javaClass.simpleName }
                }
                nextAnnouncementAt = now + ANNOUNCEMENT_INTERVAL_MILLIS
            }

            try {
                val packet = DatagramPacket(buffer, buffer.size)
                multicastSocket.receive(packet)
                val peer = parseAnnouncement(packet.data, packet.length, packet.address) ?: continue
                if (peer.deviceId == identity.deviceId || peer.fingerprint == identity.fingerprint) continue
                peers.observe(peer)
                onPeersChanged()
            } catch (_: java.net.SocketTimeoutException) {
                peers.clearExpired()
            } catch (exception: SocketException) {
                if (running.get()) error = exception.message ?: "LAN discovery failed."
                break
            } catch (exception: Exception) {
                if (running.get()) error = exception.message ?: exception.javaClass.simpleName
            }
        }
    }

    private fun announcementPackets(): List<DatagramPacket> {
        val json = JSONObject()
            .put("Version", 1)
            .put("DeviceId", identity.deviceId)
            .put("DisplayName", identity.displayName)
            .put("Port", tcpPort)
            .put("Fingerprint", identity.fingerprint)
            .put("SentAt", java.time.Instant.now().toString())
            .toString()
            .toByteArray(Charsets.UTF_8)
        require(json.size <= MAX_ANNOUNCEMENT_BYTES) { "NOVA discovery message exceeds the size limit." }
        val destinations = linkedSetOf(MULTICAST_ADDRESS)
        wifiInterface?.interfaceAddresses
            ?.mapNotNull { interfaceAddress ->
                val address = interfaceAddress.address as? Inet4Address ?: return@mapNotNull null
                directedBroadcastAddress(address, interfaceAddress.networkPrefixLength)
            }
            ?.let(destinations::addAll)
        return destinations.map { DatagramPacket(json, json.size, it, DISCOVERY_PORT) }
    }

    private fun parseAnnouncement(data: ByteArray, length: Int, address: InetAddress): NovaPeer? {
        if (length !in 1..MAX_ANNOUNCEMENT_BYTES || address !is Inet4Address) return null
        return runCatching {
            val json = JSONObject(String(data, 0, length, Charsets.UTF_8))
            val sentAt = java.time.Instant.parse(json.getString("SentAt")).toEpochMilli()
            val deviceId = json.getString("DeviceId")
            val displayName = json.getString("DisplayName")
            val port = json.getInt("Port")
            val fingerprint = PeerStore.normalizeFingerprint(json.getString("Fingerprint"))
            if (json.getInt("Version") != 1 ||
                !PeerStore.isValidDeviceId(deviceId) ||
                displayName.isBlank() || displayName.length > 80 || displayName.any(Char::isISOControl) ||
                port !in 1..65535 || !PeerStore.isValidFingerprint(fingerprint) ||
                sentAt < System.currentTimeMillis() - ANNOUNCEMENT_MAX_AGE_MILLIS ||
                sentAt > System.currentTimeMillis() + ANNOUNCEMENT_MAX_AGE_MILLIS
            ) return null
            NovaPeer(deviceId, displayName, address, port, fingerprint, System.currentTimeMillis(), false)
        }.getOrNull()
    }

    private fun findMulticastInterface(): NetworkInterface? {
        val interfaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        return interfaces.firstOrNull { network ->
            network.isUp && network.supportsMulticast() &&
                network.name.startsWith("wlan", ignoreCase = true) &&
                network.inetAddresses.toList().any { it is Inet4Address && !it.isLoopbackAddress }
        } ?: interfaces.firstOrNull { network ->
            network.isUp && network.supportsMulticast() &&
                network.inetAddresses.toList().any { it is Inet4Address && !it.isLoopbackAddress }
        }
    }

    @Synchronized
    override fun close() {
        if (!running.getAndSet(false)) return
        socket?.let { multicastSocket ->
            runCatching {
                multicastSocket.leaveGroup(
                    InetSocketAddress(MULTICAST_ADDRESS, DISCOVERY_PORT),
                    multicastSocket.networkInterface
                )
            }
            multicastSocket.close()
        }
        socket = null
        thread?.interrupt()
        thread = null
        if (multicastLock.isHeld) multicastLock.release()
    }

    companion object {
        const val DISCOVERY_PORT = 45_984
        private const val MAX_ANNOUNCEMENT_BYTES = 2_048
        private const val ANNOUNCEMENT_INTERVAL_MILLIS = 2_000L
        private const val ANNOUNCEMENT_MAX_AGE_MILLIS = 120_000L
        private val MULTICAST_ADDRESS: InetAddress = InetAddress.getByName("239.255.42.99")
    }
}

internal fun directedBroadcastAddress(address: Inet4Address, prefixLength: Short): Inet4Address? {
    val prefix = prefixLength.toInt()
    if (prefix !in 1..30) return null

    val addressBytes = address.address
    val broadcastBytes = ByteArray(addressBytes.size)
    for (index in addressBytes.indices) {
        val prefixBitsInByte = (prefix - index * 8).coerceIn(0, 8)
        val networkMask = if (prefixBitsInByte == 0) 0 else (0xFF shl (8 - prefixBitsInByte)) and 0xFF
        broadcastBytes[index] = ((addressBytes[index].toInt() and 0xFF) or (networkMask.inv() and 0xFF)).toByte()
    }
    return InetAddress.getByAddress(broadcastBytes) as Inet4Address
}
