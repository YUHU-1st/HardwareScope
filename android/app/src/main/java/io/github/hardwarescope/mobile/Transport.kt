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
    private val connection = JsonHttpsConnection(config.address, tls, config.token) { certificateFingerprint(it) == config.fingerprint }
    override fun request(method: String, path: String, body: JSONObject?) = connection.request(method, path, body)
    override fun close() = connection.close()
}

/** Only the two PIN bootstrap routes may observe an as-yet untrusted certificate. */
@SuppressLint("CustomX509TrustManager")
class HttpsPairingTransport(target: PairingTarget) : PairingTransport {
    @Volatile override var peerFingerprint: String? = null
        private set
    private fun accept(certificate: X509Certificate): Boolean {
        certificate.checkValidity()
        val observed = certificateFingerprint(certificate)
        synchronized(this) {
            val previous = peerFingerprint
            if (previous != null && previous != observed) return false
            peerFingerprint = observed
        }
        return true
    }
    private val tls = SSLContext.getInstance("TLS").apply {
        init(null, arrayOf<TrustManager>(object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("客户端证书不适用")
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val certificate = chain?.firstOrNull() ?: throw CertificateException("电脑未提供证书")
                if (!accept(certificate)) throw CertificateException("配对过程中电脑证书发生变化。")
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }), null)
    }
    private val connection = JsonHttpsConnection(target.validated().also { require(it.mode == "HTTPS") }.address, tls, null, ::accept)
    override fun request(method: String, path: String, body: JSONObject?): JSONObject {
        require(isPairingRequest(method, path)) { "PIN 配对连接只能用于验证电脑。" }
        return connection.request(method, path, body)
    }
    override fun close() = connection.close()
}

private class JsonHttpsConnection(private val address: String, private val tls: SSLContext, private val token: String?, private val verify: (X509Certificate) -> Boolean) : Transport {
    @Volatile private var active: HttpsURLConnection? = null

    override fun request(method: String, path: String, body: JSONObject?): JSONObject {
        val connection = URL(address + path).openConnection() as HttpsURLConnection
        active = connection
        try {
            connection.sslSocketFactory = tls.socketFactory
            connection.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, session ->
                val cert = session.peerCertificates.firstOrNull() as? X509Certificate
                cert != null && verify(cert)
            }
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 8000
            connection.readTimeout = 12000
            connection.requestMethod = method
            if (token != null) connection.setRequestProperty("Authorization", "Bearer $token")
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
            if (status !in 200..299) {
                val message = if (status == 401 && token != null) "手机授权已失效，请使用新 PIN 重新配对。" else json.optString("message").ifBlank { "电脑拒绝请求。" }
                throw IllegalStateException(message + "（HTTP $status）")
            }
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
        require(config.token.isNotEmpty() || isPairingRequest(method, path)) { "请先用 PIN 验证电脑。" }
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

class BluetoothPairingTransport(context: Context, target: PairingTarget) : PairingTransport {
    override val peerFingerprint: String? = null
    private val connection = BluetoothTransport(context, ConnectionConfig("Bluetooth", target.validated().also { require(it.mode == "Bluetooth") }.address, "", ""))
    override fun request(method: String, path: String, body: JSONObject?): JSONObject {
        require(isPairingRequest(method, path)) { "PIN 配对连接只能用于验证电脑。" }
        return connection.request(method, path, body)
    }
    override fun close() = connection.dispose()
}
