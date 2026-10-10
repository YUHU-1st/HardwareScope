package io.github.hardwarescope.mobile

import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import kotlin.math.max

data class DiscoveredPc(val name: String, val address: String)

object LanDiscovery {
    fun discover(): List<DiscoveredPc> {
        val found = linkedMapOf<String, DiscoveredPc>()
        val request = "HardwareScope.Discover.v1".toByteArray(Charsets.UTF_8)
        val broadcasts = linkedSetOf(InetAddress.getByName("255.255.255.255"))
        NetworkInterface.getNetworkInterfaces()?.toList()?.filter { it.isUp && !it.isLoopback }?.forEach { network ->
            network.interfaceAddresses.mapNotNull { it.broadcast }.filterIsInstance<Inet4Address>().forEach { broadcasts.add(it) }
        }
        DatagramSocket().use { socket ->
            socket.broadcast = true
            broadcasts.forEach { address -> socket.send(DatagramPacket(request, request.size, address, 47832)) }
            val until = System.nanoTime() + 1_500_000_000L
            val buffer = ByteArray(2048)
            while (found.size < 32) {
                val remaining = until - System.nanoTime()
                if (remaining <= 0) break
                socket.soTimeout = max(1, (remaining / 1_000_000L).toInt())
                val packet = DatagramPacket(buffer, buffer.size)
                try { socket.receive(packet) } catch (_: SocketTimeoutException) { break }
                parseResponse(packet)?.let { found[it.address] = it }
            }
        }
        return found.values.sortedBy { it.name }
    }

    internal fun parseResponse(packet: DatagramPacket): DiscoveredPc? {
        if (packet.address !is Inet4Address || packet.port != 47832 || packet.length >= 2048) return null
        val json = try { JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8)) } catch (_: Exception) { return null }
        if (json.optString("product") != "HardwareScope") return null
        val port = json.optInt("port")
        val name = json.optString("name").trim()
        if (port !in 1024..65535 || name.isEmpty() || name.length > 128) return null
        return DiscoveredPc(name, "https://${packet.address.hostAddress}:$port")
    }
}
