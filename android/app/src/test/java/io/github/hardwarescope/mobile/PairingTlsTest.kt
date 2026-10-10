package io.github.hardwarescope.mobile

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.InetAddress
import java.net.ServerSocket
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket

class PairingTlsTest {
    private fun context(swapped: Boolean): SSLContext {
        val store = KeyStore.getInstance("PKCS12").apply { load(this@PairingTlsTest.javaClass.getResourceAsStream("/https-test.p12"), "test-only".toCharArray()) }
        if (swapped) {
            // The same test key proves a real TLS certificate change, without another private-key fixture.
            val original = store.getCertificate("https-test") as X509Certificate
            val encoded = original.encoded
            encoded[encoded.lastIndex] = (encoded.last().toInt() xor 1).toByte()
            val changed = CertificateFactory.getInstance("X.509").generateCertificate(ByteArrayInputStream(encoded))
            store.setKeyEntry("https-test", store.getKey("https-test", "test-only".toCharArray()), "test-only".toCharArray(), arrayOf(changed))
        }
        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, "test-only".toCharArray()) }
        return SSLContext.getInstance("TLS").apply { init(keys.keyManagers, null, null) }
    }

    @Test fun secondPairRequestRejectsCertificateChangeBeforeSendingProof() {
        val first = context(false)
        val second = context(true)
        val requests = AtomicInteger()
        val sockets = AtomicInteger()
        ServerSocket(0, 5, InetAddress.getByName("127.0.0.1")).use { listener ->
            val thread = Thread {
                repeat(2) {
                    try {
                        val plain = listener.accept()
                        val tls = if (sockets.getAndIncrement() == 0) first else second
                        (tls.socketFactory.createSocket(plain, "127.0.0.1", listener.localPort, true) as SSLSocket).use { socket ->
                            socket.useClientMode = false; socket.soTimeout = 8000; socket.startHandshake()
                            val reader = BufferedReader(InputStreamReader(socket.inputStream, Charsets.US_ASCII))
                            if (reader.readLine() == null) return@use
                            var length = 0
                            while (true) {
                                val line = reader.readLine() ?: break
                                if (line.isEmpty()) break
                                if (line.startsWith("Content-Length:", true)) length = line.substringAfter(':').trim().toInt()
                            }
                            repeat(length) { reader.read() }
                            requests.incrementAndGet()
                            val data = "{}"
                            socket.outputStream.write(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: ${data.length}\r\nConnection: close\r\n\r\n$data").toByteArray(Charsets.US_ASCII))
                            socket.outputStream.flush()
                        }
                    } catch (_: java.io.IOException) { /* The switched certificate must abort the second handshake. */ }
                }
            }.apply { isDaemon = true; start() }
            val transport = HttpsPairingTransport(PairingTarget("HTTPS", "https://127.0.0.1:${listener.localPort}"))
            try {
                transport.request("POST", "/pair/start", JSONObject())
                assertThrows(javax.net.ssl.SSLException::class.java) { transport.request("POST", "/pair/finish", JSONObject().put("m1", "should-not-leave-client")) }
                assertEquals(1, requests.get())
            } finally { transport.close(); listener.close(); thread.join(1000) }
        }
    }
}
