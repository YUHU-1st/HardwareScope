package io.github.hardwarescope.mobile

import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.BufferedReader
import java.io.InputStreamReader
import java.security.KeyStore
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

class HttpsTransportTest {
    private lateinit var server: SSLServerSocket
    private lateinit var serverThread: Thread
    private lateinit var certificate: X509Certificate
    private val hits = AtomicInteger()
    @Volatile private var authorization = ""
    private val token = "b2".repeat(32)

    @Before fun startServer() {
        val store = KeyStore.getInstance("PKCS12").apply { load(this@HttpsTransportTest.javaClass.getResourceAsStream("/https-test.p12"), "test-only".toCharArray()) }
        certificate = store.getCertificate("https-test") as X509Certificate
        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, "test-only".toCharArray()) }
        val context = SSLContext.getInstance("TLS").apply { init(keys.keyManagers, null, null) }
        server = context.serverSocketFactory.createServerSocket(0, 5, java.net.InetAddress.getByName("127.0.0.1")) as SSLServerSocket
        serverThread = Thread {
            while (!server.isClosed) {
                try {
                    (server.accept() as SSLSocket).use { socket ->
                        socket.soTimeout = 8000
                        val reader = BufferedReader(InputStreamReader(socket.inputStream, Charsets.US_ASCII))
                        val first = reader.readLine() ?: return@use
                        val path = first.split(' ').getOrElse(1) { "/" }
                        var auth = ""
                        while (true) {
                            val line = reader.readLine() ?: break
                            if (line.isEmpty()) break
                            if (line.startsWith("Authorization:", true)) auth = line.substringAfter(':').trim()
                        }
                        hits.incrementAndGet()
                        if (path == "/api/snapshot") authorization = auth
                        val status = if (path == "/redirect") "307 Temporary Redirect" else "200 OK"
                        val body = when (path) {
                            "/redirect" -> "{\"message\":\"redirect rejected\"}".toByteArray()
                            "/large" -> ByteArray(2 * 1024 * 1024 + 10) { 65 }
                            else -> "{\"sequence\":42,\"machineName\":\"test-pc\"}".toByteArray()
                        }
                        val location = if (path == "/redirect") "Location: http://127.0.0.1:${server.localPort}/api/snapshot\r\n" else ""
                        socket.outputStream.write(("HTTP/1.1 $status\r\nContent-Type: application/json\r\nContent-Length: ${body.size}\r\n${location}Connection: close\r\n\r\n").toByteArray(Charsets.US_ASCII))
                        socket.outputStream.write(body); socket.outputStream.flush()
                    }
                } catch (_: java.io.IOException) { /* Rejected TLS handshakes and bounded-response disconnects are expected. */ }
            }
        }.apply { isDaemon = true; start() }
    }

    @After fun stopServer() { server.close(); serverThread.join(1000) }
    private fun config(pin: String = certificateFingerprint(certificate)) = ConnectionConfig("HTTPS", "https://127.0.0.1:${server.localPort}", pin, token).validated()

    @Test fun explicitPinAllowsSelfSignedPcCertificateAndSendsAuthentication() {
        val transport = HttpsTransport(config())
        val response = transport.request("GET", "/api/snapshot")
        assertEquals(42, response.getInt("sequence")); assertEquals("Bearer $token", authorization); assertEquals(1, hits.get())
        transport.close()
    }

    @Test fun wrongPinRejectsBeforeSendingTokenOrRequest() {
        assertThrows(javax.net.ssl.SSLException::class.java) { HttpsTransport(config("00".repeat(32))).request("GET", "/api/snapshot") }
        assertEquals(0, hits.get()); assertEquals("", authorization)
    }

    @Test fun redirectCannotDowngradeOrForwardCredentials() {
        assertThrows(IllegalStateException::class.java) { HttpsTransport(config()).request("GET", "/redirect") }
        assertEquals(1, hits.get()); assertEquals("", authorization)
    }

    @Test fun excessiveResponseIsBounded() {
        assertThrows(IllegalArgumentException::class.java) { HttpsTransport(config()).request("GET", "/large") }
    }
}
