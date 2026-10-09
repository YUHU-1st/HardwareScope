package io.github.hardwarescope.mobile

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.net.URL
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

interface Transport {
    fun request(method: String, path: String, body: JSONObject? = null): JSONObject
    fun close()
}

private const val MAX_RESPONSE_BYTES = 2 * 1024 * 1024

fun certificateFingerprint(certificate: X509Certificate): String = MessageDigest.getInstance("SHA-256")
    .digest(certificate.encoded).joinToString("") { "%02X".format(it) }

// The self-signed PC certificate is authorized by an explicit, exact SHA-256 pin.
@SuppressLint("CustomX509TrustManager")
class HttpsTransport(private val config: ConnectionConfig) : Transport {
    @Volatile private var active: HttpsURLConnection? = null
    private val tls = SSLContext.getInstance("TLS").apply {
        init(null, arrayOf<TrustManager>(object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("客户端证书不适用")
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val certificate = chain?.firstOrNull() ?: throw CertificateException("电脑未提供证书")
                if (certificateFingerprint(certificate) != config.fingerprint) throw CertificateException("电脑证书指纹不匹配，请在电脑端核对。")
                certificate.checkValidity()
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }), null)
    }

    override fun request(method: String, path: String, body: JSONObject?): JSONObject {
        val connection = URL(config.address + path).openConnection() as HttpsURLConnection
        active = connection
        try {
            connection.sslSocketFactory = tls.socketFactory
            // Trust is granted only to the explicitly pinned certificate for this connection.
            connection.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, session ->
                val cert = session.peerCertificates.firstOrNull() as? X509Certificate
                cert != null && certificateFingerprint(cert) == config.fingerprint
            }
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 8000
            connection.readTimeout = 12000
            connection.requestMethod = method
            connection.setRequestProperty("Authorization", "Bearer ${config.token}")
            connection.setRequestProperty("Accept", "application/json")
            if (body != null) {
                val bytes = body.toString().toByteArray(Charsets.UTF_8)
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.setFixedLengthStreamingMode(bytes.size)
                connection.outputStream.use { it.write(bytes) }
            }
            val status = connection.responseCode
            val stream = if (status in 200..299) connection.inputStream else connection.errorStream
            val data = stream?.use { input ->
                val out = ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                while (true) {
                    val count = input.read(buffer)
                    if (count == -1) break
                    require(out.size() + count <= MAX_RESPONSE_BYTES) { "电脑返回的数据超过 2 MB 限制。" }
                    out.write(buffer, 0, count)
                }
                String(out.toByteArray(), Charsets.UTF_8)
            }.orEmpty()
            val json = try { JSONObject(data) } catch (_: Exception) { throw IllegalStateException("电脑响应不是有效 JSON（HTTP $status）。") }
            if (status !in 200..299) throw IllegalStateException(json.optString("message").ifBlank { if (status == 401) "访问密钥不正确或已轮换。" else "电脑拒绝请求。" } + "（HTTP $status）")
            return json
        } finally {
            connection.disconnect()
            if (active === connection) active = null
        }
    }

    override fun close() { active?.disconnect(); active = null }
}

class BluetoothTransport(private val context: Context, private val config: ConnectionConfig) : Transport {
    @Volatile private var socket: BluetoothSocket? = null
    private var input: BufferedInputStream? = null
    private val timer = Executors.newSingleThreadScheduledExecutor()

    @SuppressLint("MissingPermission")
    private fun connect() {
        if (socket?.isConnected == true) return
        val adapter = context.getSystemService(BluetoothManager::class.java)?.adapter ?: throw IllegalStateException("手机不支持蓝牙。")
        check(adapter.isEnabled) { "请在手机系统设置中打开蓝牙。" }
        val candidate = adapter.getRemoteDevice(config.address).createRfcommSocketToServiceRecord(UUID.fromString("9174d900-1895-4d3e-a219-3a1927f05b81"))
        socket = candidate
        candidate.connect()
        input = BufferedInputStream(candidate.inputStream)
    }

    override fun request(method: String, path: String, body: JSONObject?): JSONObject {
        val timeout = timer.schedule({ socket?.close() }, 12, TimeUnit.SECONDS)
        try {
            connect()
            val id = UUID.randomUUID().toString()
            val request = JSONObject().put("id", id).put("token", config.token).put("method", method).put("path", path)
            if (body != null) request.put("body", body)
            socket!!.outputStream.apply { write((request.toString() + "\n").toByteArray(Charsets.UTF_8)); flush() }
            val bytes = ByteArrayOutputStream()
            while (true) {
                val next = input!!.read()
                check(next != -1) { "电脑已断开蓝牙连接。" }
                if (next == 10) break
                require(bytes.size() < MAX_RESPONSE_BYTES) { "电脑返回的数据超过 2 MB 限制。" }
                bytes.write(next)
            }
            val response = JSONObject(String(bytes.toByteArray(), Charsets.UTF_8))
            check(response.optString("id") == id) { "蓝牙响应标识不匹配。" }
            val result = response.optJSONObject("body") ?: throw IllegalStateException("蓝牙响应内容无效。")
            val status = response.optInt("status", 500)
            if (status !in 200..299) throw IllegalStateException(result.optString("message").ifBlank { "电脑拒绝蓝牙请求（$status）。" })
            return result
        } catch (e: Exception) {
            socket?.close(); socket = null; input = null
            throw e
        } finally { timeout.cancel(false) }
    }

    override fun close() { socket?.close(); socket = null; input = null }
    fun dispose() { close(); timer.shutdownNow() }
}
