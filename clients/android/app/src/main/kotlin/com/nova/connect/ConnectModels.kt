package com.nova.connect

import java.net.InetAddress

data class NovaPeer(
    val deviceId: String,
    val displayName: String,
    val address: InetAddress,
    val port: Int,
    val fingerprint: String,
    val lastSeenMillis: Long,
    val trusted: Boolean,
    val identityChanged: Boolean = false
) {
    val online: Boolean
        get() = System.currentTimeMillis() - lastSeenMillis < PEER_EXPIRY_MILLIS

    companion object {
        const val PEER_EXPIRY_MILLIS = 8_000L
    }
}

data class TransferOfferData(
    val transferId: String,
    val senderId: String,
    val senderName: String,
    val senderPlatform: String,
    val recipientId: String,
    val fileName: String,
    val sizeBytes: Long,
    val contentType: String?,
    val sha256: String,
    val createdAt: String,
    val expiresAt: String
)

data class IncomingRequest(
    val offer: TransferOfferData,
    val peerName: String
)

data class TransferRow(
    val transferId: String,
    val fileName: String,
    val deviceName: String,
    val direction: String,
    val status: String,
    val bytesTransferred: Long,
    val totalBytes: Long,
    val bytesPerSecond: Long = 0,
    val updatedAtMillis: Long = System.currentTimeMillis(),
    val error: String? = null
) {
    val progress: Int
        get() = if (totalBytes <= 0) 0 else ((bytesTransferred * 100) / totalBytes).toInt().coerceIn(0, 100)

    val isActive: Boolean
        get() = status in setOf("Queued", "Preparing", "Offered", "Waiting for approval", "Accepted", "Transferring")
}
