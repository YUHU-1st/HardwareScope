package io.github.hardwarescope.mobile

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import java.io.File

/** Optional JVM integration against the real PC service; no user process is controlled. */
class PcIntegrationTest {
    private lateinit var config: ConnectionConfig

    @Before fun readPrivateConnectionFile() {
        val filename = System.getenv("HARDWARESCOPE_PC_INTEGRATION_FILE")
        assumeTrue("PC integration is enabled only with an explicit private connection file.", !filename.isNullOrBlank())
        val json = JSONObject(File(filename!!).readText(Charsets.UTF_8))
        config = ConnectionConfig("HTTPS", json.getString("address"), json.getString("fingerprint"), json.getString("token")).validated()
    }

    @Test fun realPcSnapshotAndSettingsRoundTrip() {
        val transport = HttpsTransport(config)
        val snapshot = transport.request("GET", "/api/snapshot")
        assertTrue(snapshot.getString("machineName").isNotBlank())
        assertTrue(snapshot.has("sequence") && snapshot.has("timestamp") && snapshot.has("diagnostics"))
        val hardware = snapshot.getJSONArray("hardware")
        assertTrue("PC must return hardware inventory", hardware.length() > 0)
        val categories = setOf("Motherboard", "CPU", "GPU", "NPU", "Memory", "VRAM", "Network", "Storage")
        for (i in 0 until hardware.length()) {
            val item = hardware.getJSONObject(i)
            assertTrue(categories.contains(item.getString("category")))
            assertTrue(item.getString("id").isNotBlank())
            val oc = item.getJSONObject("overclock")
            assertTrue(oc.has("offsetMHz") && oc.has("percent") && oc.has("evidence") && oc.has("attribution"))
            assertTrue(oc.isNull("offsetMHz") || oc.get("offsetMHz") is Number)
            val sensors = item.getJSONArray("sensors")
            for (j in 0 until sensors.length()) {
                val sensor = sensors.getJSONObject(j)
                assertTrue(sensor.has("id") && sensor.has("kind") && sensor.has("unit") && sensor.has("source") && sensor.has("value"))
                assertTrue(sensor.isNull("value") || sensor.get("value") is Number)
            }
        }
        val processes = snapshot.getJSONArray("processes")
        assertTrue("PC must return the process list", processes.length() > 0)
        val process = processes.getJSONObject(0)
        listOf("pid", "instanceId", "name", "cpuPercent", "memoryMb", "gpuPercent", "npuPercent", "vramMb", "ioReadBytesPerSecond", "ioWriteBytesPerSecond", "networkBytesPerSecond", "priority", "canControl").forEach { assertTrue("Missing process field $it", process.has(it)) }
        assertTrue(process.getString("instanceId").isNotBlank())
        val connection = transport.request("GET", "/api/connection")
        assertTrue("Remote connection endpoint must not expose the secret", connection.optString("token").isEmpty())
        assertTrue(connection.has("bluetoothStatus") && connection.has("remoteControlEnabled"))
        val original = transport.request("GET", "/api/settings")
        try {
            val updated = JSONObject(original.toString()).put("samplingIntervalMs", 3000)
            assertTrue(transport.request("PUT", "/api/settings", updated).getBoolean("success"))
            assertEquals(3000, transport.request("GET", "/api/settings").getInt("samplingIntervalMs"))
        } finally {
            transport.request("PUT", "/api/settings", original)
            transport.close()
        }
    }

    @Test fun realPcRejectsWrongTokenAndWrongCertificatePin() {
        val denied = assertThrows(IllegalStateException::class.java) { HttpsTransport(config.copy(token = "00".repeat(32))).request("GET", "/api/snapshot") }
        assertTrue("An invalid access key must return HTTP 401", denied.message.orEmpty().contains("HTTP 401"))
        assertThrows(javax.net.ssl.SSLException::class.java) { HttpsTransport(config.copy(fingerprint = "00".repeat(32))).request("GET", "/api/snapshot") }
    }
}
