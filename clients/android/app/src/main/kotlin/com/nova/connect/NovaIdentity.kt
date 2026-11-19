package com.nova.connect

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.util.Date
import java.util.Locale
import javax.security.auth.x500.X500Principal

class NovaIdentity(context: Context) {
    val deviceId: String
    val displayName: String
    val certificate: X509Certificate
    val privateKey: PrivateKey
    val fingerprint: String
    val pairingCode: String

    private val keyStore: KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    init {
        val preferences = context.getSharedPreferences("nova-connect-identity", Context.MODE_PRIVATE)
        val storedId = preferences.getString("deviceId", null)
            ?.takeIf(PeerStore::isValidDeviceId)
        deviceId = storedId ?: java.util.UUID.randomUUID().toString().replace("-", "").lowercase(Locale.ROOT)
        val obsoleteAliases = listOf("nova-connect-$deviceId", "nova-connect-tls-v2-$deviceId")
        val alias = "nova-connect-tls-v3-$deviceId"
        if (!keyStore.containsAlias(alias)) {
            val now = System.currentTimeMillis()
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_RSA, "AndroidKeyStore")
            generator.initialize(
                KeyGenParameterSpec.Builder(
                    alias,
                    KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_DECRYPT
                )
                    .setKeySize(2048)
                    .setDigests(
                        KeyProperties.DIGEST_NONE,
                        KeyProperties.DIGEST_SHA256,
                        KeyProperties.DIGEST_SHA384,
                        KeyProperties.DIGEST_SHA512
                    )
                    .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1, KeyProperties.SIGNATURE_PADDING_RSA_PSS)
                    .setEncryptionPaddings(
                        KeyProperties.ENCRYPTION_PADDING_RSA_PKCS1,
                        KeyProperties.ENCRYPTION_PADDING_NONE
                    )
                    .setCertificateSubject(X500Principal("CN=NOVA TaskBar $deviceId"))
                    .setCertificateSerialNumber(BigInteger.ONE)
                    .setCertificateNotBefore(Date(now - 5 * 60_000L))
                    .setCertificateNotAfter(Date(now + 5L * 365 * 24 * 60 * 60 * 1000))
                    .build()
            )
            generator.generateKeyPair()
        }

        preferences.edit().putString("deviceId", deviceId).apply()
        displayName = preferences.getString("displayName", null)
            ?.takeIf { it.isNotBlank() && it.length <= 80 }
            ?: (Build.MODEL?.takeIf(String::isNotBlank) ?: "Android device").take(80)
                .also { preferences.edit().putString("displayName", it).apply() }

        certificate = keyStore.getCertificate(alias) as? X509Certificate
            ?: throw IllegalStateException("AndroidKeyStore did not return the NOVA device certificate.")
        privateKey = keyStore.getKey(alias, null) as? PrivateKey
            ?: throw IllegalStateException("AndroidKeyStore did not return the NOVA device key.")
        obsoleteAliases.filter(keyStore::containsAlias).forEach(keyStore::deleteEntry)
        fingerprint = MessageDigest.getInstance("SHA-256")
            .digest(certificate.encoded)
            .joinToString("") { "%02X".format(Locale.ROOT, it) }
        pairingCode = PairingCode.encode(deviceId, displayName, fingerprint)
    }

    companion object {
        fun deviceIdFromCertificate(certificate: X509Certificate): String? {
            val commonName = certificate.subjectX500Principal.getName(X500Principal.RFC2253)
                .split(',')
                .firstOrNull { it.startsWith("CN=", ignoreCase = true) }
                ?.substringAfter('=')
                ?: return null
            val prefix = "NOVA TaskBar "
            if (!commonName.startsWith(prefix, ignoreCase = false)) return null
            val id = commonName.removePrefix(prefix)
            return id.takeIf(PeerStore::isValidDeviceId)
        }
    }
}

object PairingCode {
    fun encode(deviceId: String, displayName: String, fingerprint: String): String {
        val payload = org.json.JSONObject()
            .put("DeviceId", deviceId)
            .put("DisplayName", displayName)
            .put("Fingerprint", PeerStore.normalizeFingerprint(fingerprint))
            .toString()
            .toByteArray(Charsets.UTF_8)
        return "NOVA1:" + android.util.Base64.encodeToString(
            payload,
            android.util.Base64.URL_SAFE or android.util.Base64.NO_WRAP or android.util.Base64.NO_PADDING
        )
    }

    fun decode(value: String): PairingData {
        val code = value.trim()
        require(code.startsWith("NOVA1:")) { "Pairing code must start with NOVA1:." }
        val encoded = code.substringAfter("NOVA1:").filterNot(Char::isWhitespace)
        require(encoded.length in 1..4096) { "Pairing code length is invalid." }
        val decoded = android.util.Base64.decode(
            encoded,
            android.util.Base64.URL_SAFE or android.util.Base64.NO_WRAP or android.util.Base64.NO_PADDING
        )
        val json = org.json.JSONObject(String(decoded, Charsets.UTF_8))
        val id = json.getString("DeviceId")
        val name = json.getString("DisplayName")
        val fingerprint = PeerStore.normalizeFingerprint(json.getString("Fingerprint"))
        require(PeerStore.isValidDeviceId(id)) { "Pairing code device ID is invalid." }
        require(name.isNotBlank() && name.length <= 80 && name.none(Char::isISOControl)) {
            "Pairing code device name is invalid."
        }
        require(PeerStore.isValidFingerprint(fingerprint)) { "Pairing code fingerprint is invalid." }
        return PairingData(id, name, fingerprint)
    }
}

data class PairingData(val deviceId: String, val displayName: String, val fingerprint: String)
