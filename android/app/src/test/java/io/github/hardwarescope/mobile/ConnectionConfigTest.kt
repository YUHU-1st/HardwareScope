package io.github.hardwarescope.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ConnectionConfigTest {
    private val pin = "a1".repeat(32)
    private val token = "b2".repeat(32)

    @Test fun acceptsHttpsAndNormalizesExplicitPin() {
        val config = ConnectionConfig("HTTPS", "https://192.168.1.50:47831/", pin.chunked(2).joinToString(":"), token).validated()
        assertEquals("https://192.168.1.50:47831", config.address)
        assertEquals(pin.uppercase(), config.fingerprint)
    }

    @Test fun rejectsPlaintextCredentialsAndRedirectPaths() {
        listOf("http://192.168.1.50:47831", "https://user:password@192.168.1.50:47831", "https://192.168.1.50/api", "https://192.168.1.50?token=secret", "https://192.168.1.50#fragment", "https://192.168.1.50:99999").forEach { address ->
            assertThrows(IllegalArgumentException::class.java) { ConnectionConfig("HTTPS", address, pin, token).validated() }
        }
    }

    @Test fun rejectsMissingPinAndHeaderInjection() {
        assertThrows(IllegalArgumentException::class.java) { ConnectionConfig("HTTPS", "https://pc.local:47831", "abc", token).validated() }
        assertThrows(IllegalArgumentException::class.java) { ConnectionConfig("HTTPS", "https://pc.local:47831", pin, "secret\r\nX-Header: bad").validated() }
    }

    @Test fun bluetoothUsesPairedAddressAndStillRequiresToken() {
        assertEquals("A1:B2:C3:D4:E5:F6", ConnectionConfig("Bluetooth", "a1:b2:c3:d4:e5:f6", "", token).validated().address)
        assertThrows(IllegalArgumentException::class.java) { ConnectionConfig("Bluetooth", "select a device", "", token).validated() }
        assertThrows(IllegalArgumentException::class.java) { ConnectionConfig("Bluetooth", "A1:B2:C3:D4:E5:F6", "", "").validated() }
    }
}
