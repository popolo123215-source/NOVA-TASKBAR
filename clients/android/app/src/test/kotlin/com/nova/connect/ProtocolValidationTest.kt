package com.nova.connect

import org.junit.Assert.assertFalse
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.Inet4Address
import java.net.InetAddress

class ProtocolValidationTest {
    @Test
    fun restrictsAndroidTransfersToCompatibleTls12() {
        assertArrayEquals(
            arrayOf("TLSv1.2"),
            compatibleTlsProtocols(arrayOf("TLSv1.3", "TLSv1.2"))
        )
        assertThrows(IllegalArgumentException::class.java) {
            compatibleTlsProtocols(arrayOf("TLSv1.3"))
        }
    }

    @Test
    fun computesDirectedBroadcastForWifiSubnet() {
        val address = InetAddress.getByName("192.168.10.23") as Inet4Address
        assertEquals(
            InetAddress.getByName("192.168.10.255"),
            directedBroadcastAddress(address, 24)
        )
        assertEquals(null, directedBroadcastAddress(address, 31))
    }

    @Test
    fun acceptsOrdinaryAndUnicodeFileNames() {
        assertTrue(NovaWire.isSafeFileName("photo-01.jpg"))
        assertTrue(NovaWire.isSafeFileName("запись 1.ogg"))
        assertTrue(NovaWire.isSafeFileName("archive.tar.gz"))
    }

    @Test
    fun rejectsPathSyntaxAndInvalidWindowsNames() {
        listOf("", ".", "..", "../escape.txt", "folder\\file.txt", "drive:C.txt",
            "trailing.", "trailing ", "control\u0001.txt").forEach {
            assertFalse("Expected unsafe filename: $it", NovaWire.isSafeFileName(it))
        }
        assertFalse(NovaWire.isSafeFileName("x".repeat(NovaWire.MAX_FILE_NAME_LENGTH + 1)))
    }

    @Test
    fun validatesAndNormalizesPeerIdentityValues() {
        val fingerprint = "ab".repeat(32)
        assertTrue(PeerStore.isValidFingerprint(fingerprint))
        assertTrue(PeerStore.normalizeFingerprint(fingerprint) == fingerprint.uppercase())
        assertFalse(PeerStore.isValidFingerprint("ab".repeat(31)))
        assertTrue(PeerStore.isValidDeviceId("0123456789abcdef0123456789abcdef"))
        assertFalse(PeerStore.isValidDeviceId("not-a-device-id"))
    }
}
