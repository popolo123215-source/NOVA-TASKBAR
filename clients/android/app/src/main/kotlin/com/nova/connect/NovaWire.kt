package com.nova.connect

import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.time.Instant
import java.util.UUID

object NovaWire {
    const val VERSION = 1
    const val MAX_FRAME_LENGTH = 64 * 1024
    const val MAX_FILE_NAME_LENGTH = 255

    fun writeFrame(output: OutputStream, type: String, payload: JSONObject) {
        val bytes = JSONObject()
            .put("Version", VERSION)
            .put("Type", type)
            .put("Payload", payload)
            .toString()
            .toByteArray(Charsets.UTF_8)
        require(bytes.isNotEmpty() && bytes.size <= MAX_FRAME_LENGTH) { "NOVA message exceeds 64 KiB." }
        val stream = DataOutputStream(output)
        stream.writeInt(bytes.size)
        stream.write(bytes)
        stream.flush()
    }

    fun readFrame(input: InputStream): WireMessage {
        val stream = DataInputStream(input)
        val length = try {
            stream.readInt()
        } catch (eof: EOFException) {
            throw EOFException("The NOVA peer disconnected before sending a frame.")
        }
        require(length in 1..MAX_FRAME_LENGTH) { "Invalid NOVA frame length." }
        val bytes = ByteArray(length)
        stream.readFully(bytes)
        val envelope = JSONObject(String(bytes, Charsets.UTF_8))
        require(envelope.getInt("Version") == VERSION) { "Unsupported NOVA protocol version." }
        return WireMessage(envelope.getString("Type"), envelope.getJSONObject("Payload"))
    }

    fun createOffer(
        transferId: String,
        senderId: String,
        senderName: String,
        recipientId: String,
        recipientName: String,
        fileName: String,
        sizeBytes: Long,
        contentType: String?,
        sha256: String
    ): JSONObject {
        require(isSafeFileName(fileName)) { "The selected provider returned an unsafe file name." }
        require(sizeBytes >= 0) { "File size is unavailable or invalid." }
        require(PeerStore.isValidFingerprint(sha256)) { "Invalid SHA-256 digest." }
        val now = Instant.now()
        return JSONObject()
            .put("TransferId", transferId)
            .put("Sender", deviceReference(senderId, senderName, "android"))
            .put("Recipient", deviceReference(recipientId, recipientName, "windows"))
            .put("FileName", fileName)
            .put("SizeBytes", sizeBytes)
            .put("ContentType", contentType ?: JSONObject.NULL)
            .put("Sha256", sha256.uppercase())
            .put("CreatedAt", now.toString())
            .put("ExpiresAt", now.plusSeconds(180).toString())
            .put("CanResume", false)
            .put("TransportPreference", 0)
    }

    fun parseOffer(payload: JSONObject, localDeviceId: String, authenticatedPeerId: String): TransferOfferData {
        val transferId = payload.getString("TransferId")
        val sender = payload.getJSONObject("Sender")
        val recipient = payload.getJSONObject("Recipient")
        val senderId = sender.getString("DeviceId")
        val recipientId = recipient.getString("DeviceId")
        val fileName = payload.getString("FileName")
        val sizeBytes = payload.getLong("SizeBytes")
        val digest = payload.getString("Sha256").uppercase()
        val createdAt = Instant.parse(payload.getString("CreatedAt"))
        val expiresAt = Instant.parse(payload.getString("ExpiresAt"))
        require(transferId.length == 32 && transferId.all { it in '0'..'9' || it.lowercaseChar() in 'a'..'f' }) {
            "Invalid transfer ID."
        }
        require(senderId == authenticatedPeerId) { "The file offer does not match the authenticated device." }
        require(recipientId == localDeviceId) { "The file offer is addressed to another device." }
        require(isSafeFileName(fileName)) { "The incoming file name is unsafe." }
        require(sizeBytes >= 0) { "The incoming file size is invalid." }
        require(PeerStore.isValidFingerprint(digest)) { "The incoming file digest is invalid." }
        require(!createdAt.isAfter(Instant.now().plusSeconds(120)) &&
            !createdAt.isBefore(Instant.now().minusSeconds(600)) &&
            expiresAt.isAfter(createdAt) && expiresAt.isAfter(Instant.now()) &&
            !expiresAt.isAfter(Instant.now().plusSeconds(600))
        ) { "The file offer has expired or has an invalid time window." }
        val contentType = payload.optString("ContentType").takeUnless { it.isBlank() || it == "null" }
        return TransferOfferData(
            transferId = transferId,
            senderId = senderId,
            senderName = sender.getString("DisplayName").take(80),
            senderPlatform = sender.optString("Platform", "windows").take(20),
            recipientId = recipientId,
            fileName = fileName,
            sizeBytes = sizeBytes,
            contentType = contentType,
            sha256 = digest,
            createdAt = createdAt.toString(),
            expiresAt = expiresAt.toString()
        )
    }

    fun reply(accepted: Boolean, message: String? = null): JSONObject =
        JSONObject().put("Accepted", accepted).apply {
            if (message != null) put("Message", message.take(256))
        }

    fun isSafeFileName(value: String): Boolean =
        value.isNotBlank() && value.length <= MAX_FILE_NAME_LENGTH &&
            value != "." && value != ".." &&
            !value.endsWith(' ') && !value.endsWith('.') &&
            value.none { it.isISOControl() || it in charArrayOf('/', '\\', ':', '*', '?', '"', '<', '>', '|') }

    private fun deviceReference(id: String, name: String, platform: String): JSONObject =
        JSONObject()
            .put("DeviceId", id)
            .put("DisplayName", name.take(80))
            .put("Platform", platform)

    fun newTransferId(): String = UUID.randomUUID().toString().replace("-", "")
}

data class WireMessage(val type: String, val payload: JSONObject)
