package com.nova.connect

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.net.InetAddress
import java.util.concurrent.ConcurrentHashMap

class PeerStore(context: Context) {
    private val preferences = context.getSharedPreferences("nova-connect-peers", Context.MODE_PRIVATE)
    private val trusted = ConcurrentHashMap<String, NovaPeer>()
    private val discovered = ConcurrentHashMap<String, NovaPeer>()

    init {
        val saved = runCatching { JSONArray(preferences.getString("trusted", "[]")) }.getOrNull()
        if (saved != null) {
            for (index in 0 until saved.length()) {
                val item = saved.optJSONObject(index) ?: continue
                val peer = runCatching {
                    NovaPeer(
                        item.getString("deviceId"),
                        item.getString("displayName"),
                        InetAddress.getByName(item.getString("address")),
                        item.getInt("port"),
                        normalizeFingerprint(item.getString("fingerprint")),
                        0,
                        true
                    )
                }.getOrNull() ?: continue
                if (isValidDeviceId(peer.deviceId) && isValidFingerprint(peer.fingerprint) &&
                    peer.port in 1..65535 && peer.displayName.length <= 80
                ) trusted[peer.deviceId] = peer
            }
        }
    }

    fun allPeers(): List<NovaPeer> {
        val now = System.currentTimeMillis()
        val result = HashMap<String, NovaPeer>()
        discovered.values.forEach { found ->
            val saved = trusted[found.deviceId]
            result[found.deviceId] = if (saved == null) found else found.copy(
                trusted = true,
                identityChanged = !saved.fingerprint.equals(found.fingerprint, ignoreCase = true)
            )
        }
        trusted.values.forEach { saved ->
            result.putIfAbsent(saved.deviceId, saved.copy(lastSeenMillis = 0))
        }
        return result.values.sortedWith(compareByDescending<NovaPeer> { it.online && !it.identityChanged }
            .thenBy(String.CASE_INSENSITIVE_ORDER) { it.displayName })
    }

    fun allTrustedPeers(): List<NovaPeer> = trusted.values.toList()

    fun observe(peer: NovaPeer) {
        val saved = trusted[peer.deviceId]
        discovered[peer.deviceId] = peer.copy(
            trusted = saved != null,
            identityChanged = saved != null && !saved.fingerprint.equals(peer.fingerprint, ignoreCase = true)
        )
    }

    fun trustedByFingerprint(fingerprint: String): NovaPeer? {
        val normalized = normalizeFingerprint(fingerprint)
        return trusted.values.firstOrNull { it.fingerprint.equals(normalized, ignoreCase = true) }
    }

    fun trustedByDeviceId(deviceId: String): NovaPeer? = trusted[deviceId]

    fun trust(peer: NovaPeer) {
        require(isValidDeviceId(peer.deviceId)) { "Invalid NOVA device ID." }
        require(isValidFingerprint(peer.fingerprint)) { "Invalid certificate fingerprint." }
        require(peer.displayName.isNotBlank() && peer.displayName.length <= 80) { "Invalid device name." }
        require(peer.port in 1..65535) { "Invalid device port." }
        val existing = trusted[peer.deviceId]
        require(existing == null || existing.fingerprint.equals(peer.fingerprint, ignoreCase = true)) {
            "This device ID is already paired with a different certificate."
        }
        require(trusted.values.none {
            it.deviceId != peer.deviceId && it.fingerprint.equals(peer.fingerprint, ignoreCase = true)
        }) { "This certificate is already paired to a different device ID." }
        trusted[peer.deviceId] = peer.copy(fingerprint = normalizeFingerprint(peer.fingerprint), trusted = true)
        discovered[peer.deviceId] = peer.copy(fingerprint = normalizeFingerprint(peer.fingerprint), trusted = true)
        save()
    }

    fun remove(deviceId: String) {
        trusted.remove(deviceId)
        save()
    }

    fun clearExpired() {
        val expiry = System.currentTimeMillis() - NovaPeer.PEER_EXPIRY_MILLIS
        discovered.entries.removeIf { it.value.lastSeenMillis < expiry }
    }

    private fun save() {
        val items = JSONArray()
        trusted.values.sortedBy { it.deviceId }.forEach { peer ->
            items.put(JSONObject()
                .put("deviceId", peer.deviceId)
                .put("displayName", peer.displayName)
                .put("address", peer.address.hostAddress)
                .put("port", peer.port)
                .put("fingerprint", peer.fingerprint))
        }
        preferences.edit().putString("trusted", items.toString()).apply()
    }

    companion object {
        fun normalizeFingerprint(value: String): String =
            value.replace(":", "").replace("-", "").uppercase()

        fun isValidFingerprint(value: String): Boolean =
            value.length == 64 && value.all { it in '0'..'9' || it in 'A'..'F' || it in 'a'..'f' }

        fun isValidDeviceId(value: String): Boolean =
            value.length == 32 && value.all { it in '0'..'9' || it.lowercaseChar() in 'a'..'f' }
    }
}
