package io.github.hardwarescope.mobile

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.DatagramPacket
import java.net.InetAddress

class LanDiscoveryTest {
    private fun response(json: JSONObject, port: Int = 47832, sender: String = "192.168.1.23"): DatagramPacket {
        val bytes = json.toString().toByteArray(Charsets.UTF_8)
        return DatagramPacket(bytes, bytes.size, InetAddress.getByName(sender), port)
    }
    private fun valid() = JSONObject().put("product", "HardwareScope").put("name", "测试电脑").put("port", 47831)

    @Test fun addressAlwaysComesFromUdpSenderRatherThanResponse() {
        val pc = LanDiscovery.parseResponse(response(valid().put("address", "https://attacker.example")))!!
        assertEquals("https://192.168.1.23:47831", pc.address)
        assertEquals("测试电脑", pc.name)
    }

    @Test fun rejectsDifferentProductsInvalidPortsAndUnboundedNames() {
        assertNull(LanDiscovery.parseResponse(response(valid().put("product", "other"))))
        assertNull(LanDiscovery.parseResponse(response(valid().put("port", 65536))))
        assertNull(LanDiscovery.parseResponse(response(valid().put("name", "x".repeat(129)))))
        assertNull(LanDiscovery.parseResponse(response(valid(), port = 9999)))
    }

    @Test fun discoveryNeverGrantsCertificateOrTokenTrust() {
        val pc = LanDiscovery.parseResponse(response(valid().put("token", "secret").put("fingerprint", "00".repeat(32))))!!
        assertEquals(DiscoveredPc("测试电脑", "https://192.168.1.23:47831"), pc)
    }
}
