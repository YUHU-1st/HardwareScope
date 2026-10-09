package io.github.hardwarescope.mobile

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TemperatureOverviewTest {
    @Test fun anUnavailableFirstSourceCannotHideLaterReadableTemperatures() {
        val device = JSONObject("""{"sensors":[
            {"id":"unavailable","kind":"Temperature","value":null,"source":"driver","unavailableReason":"未提供"},
            {"id":"core","kind":"Temperature","value":61.5,"source":"core sensor"},
            {"id":"package","kind":"Temperature","value":72.0,"source":"package sensor"}
        ]}""")
        val readings = temperatureReadings(device)
        assertEquals(listOf("package", "core", "unavailable"), readings.map { it.getString("id") })
        assertEquals("package sensor", readings.first().getString("source"))
        assertTrue(readings.last().isNull("value"))
    }

    @Test fun frequencyAndOtherMetricsNeverBecomeATemperature() {
        val device = JSONObject("""{"sensors":[
            {"id":"clock","kind":"Clock","value":4200,"unit":"MHz"},
            {"id":"load","kind":"Load","value":35,"unit":"%"},
            {"id":"power","kind":"Power","value":40,"unit":"W"}
        ]}""")
        assertTrue(temperatureReadings(device).isEmpty())
    }

    @Test fun missingReadingsKeepTheirReasonAndNullValue() {
        val device = JSONObject("""{"sensors":[{"id":"npu-temperature","kind":"Temperature","value":null,"unavailableReason":"NPU 驱动未公开温度"}]}""")
        val reading = temperatureReadings(device).single()
        assertTrue(reading.isNull("value"))
        assertEquals("NPU 驱动未公开温度", reading.getString("unavailableReason"))
    }

    @Test fun everyHardwareCategoryUsesItsOwnTemperatureSources() {
        listOf("Motherboard", "CPU", "GPU", "NPU", "Memory", "VRAM", "Storage", "Network").forEach { category ->
            val device = JSONObject().put("category", category).put("sensors", JSONArray().put(JSONObject().put("id", "$category/temperature").put("kind", "Temperature").put("value", 38.0)))
            assertEquals("$category/temperature", temperatureReadings(device).single().getString("id"))
        }
    }

    @Test fun deviceTemperaturePrecedesComponentSourcesAndThresholdsAreExcluded() {
        listOf("CPU" to "CPU (Tctl/Tdie)", "GPU" to "GPU 核心温度", "Storage" to "Composite").forEach { (category, preferred) ->
            val device = JSONObject().put("category", category).put("sensors", JSONArray()
                .put(JSONObject().put("id", "component").put("name", if (category == "GPU") "GPU Hot Spot" else "Temperature #1").put("kind", "Temperature").put("value", 52.0))
                .put(JSONObject().put("id", "preferred").put("name", preferred).put("kind", "Temperature").put("value", 45.0))
                .put(JSONObject().put("id", "threshold").put("name", "Critical Temperature").put("kind", "Other").put("value", 88.0)))
            assertEquals(listOf("preferred", "component"), temperatureReadings(device).map { it.getString("id") })
        }
    }
}
