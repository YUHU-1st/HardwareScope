package io.github.hardwarescope.mobile

import org.bouncycastle.crypto.agreement.srp.SRP6Server
import org.bouncycastle.crypto.agreement.srp.SRP6StandardGroups
import org.bouncycastle.crypto.agreement.srp.SRP6VerifierGenerator
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.util.BigIntegers
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.math.BigInteger
import java.security.SecureRandom
import java.time.Instant
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class PinPairingTest {
    private val target = PairingTarget("HTTPS", "https://127.0.0.1:47831")

    @Test fun leadingZeroPinCompletesMutualProofAndDecryptsCredentials() {
        val server = TestPairingServer("001234")
        val config = PinPairingClient().pair(target, "001234", server)
        assertEquals(server.token, config.token)
        assertEquals(server.fingerprint.uppercase(), config.fingerprint)
        assertEquals(2, server.requests)
        assertTrue(server.authenticated)
    }

    @Test fun wrongPinCannotObtainCredentials() {
        val server = TestPairingServer("001234")
        assertThrows(IllegalStateException::class.java) { PinPairingClient().pair(target, "001235", server) }
        assertFalse(server.authenticated)
    }

    @Test fun pinIsExactlySixAsciiDigitsAndNotNormalizedAsNumber() {
        listOf("12345", "1234567", " 12345", "１２３４５６", "12345a").forEach { pin ->
            val server = TestPairingServer("123456")
            assertThrows(IllegalArgumentException::class.java) { PinPairingClient().pair(target, pin, server) }
            assertEquals(0, server.requests)
        }
    }

    @Test fun wrongServerProofIsRejectedBeforeDecrypting() {
        assertThrows(IllegalStateException::class.java) { PinPairingClient().pair(target, "123456", TestPairingServer("123456", badProof = true)) }
    }

    @Test fun exchangedCertificateCannotBeSavedAsTrustedPc() {
        assertThrows(IllegalStateException::class.java) { PinPairingClient().pair(target, "123456", TestPairingServer("123456", changedCertificate = true)) }
    }

    @Test fun encryptedCredentialTamperingIsRejected() {
        assertThrows(javax.crypto.AEADBadTagException::class.java) { PinPairingClient().pair(target, "123456", TestPairingServer("123456", badTag = true)) }
    }

    @Test fun invalidServerPublicValueCannotBeUsed() {
        assertThrows(IllegalArgumentException::class.java) { PinPairingClient().pair(target, "123456", TestPairingServer("123456", badPublic = true)) }
    }

    @Test fun bluetoothUsesSamePinProofWithoutTlsBootstrap() {
        val server = TestPairingServer("123456")
        val config = PinPairingClient().pair(PairingTarget("Bluetooth", "a1:b2:c3:d4:e5:f6"), "123456", server)
        assertEquals("A1:B2:C3:D4:E5:F6", config.address)
        assertEquals(server.token, config.token)
    }
}

/** An actual lightweight SRP server, not a mocked successful result. */
internal class TestPairingServer(pin: String, private val badProof: Boolean = false, private val changedCertificate: Boolean = false, private val badTag: Boolean = false, private val badPublic: Boolean = false) : PairingTransport {
    val token = "b2".repeat(32)
    val fingerprint = "a1".repeat(32)
    override var peerFingerprint: String? = fingerprint
    var requests = 0
    var authenticated = false
    private val group = SRP6StandardGroups.rfc5054_3072
    private val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
    private val session = "19".repeat(16)
    private val srp = SRP6Server().apply {
        val generator = SRP6VerifierGenerator().apply { init(group, SHA256Digest()) }
        val verifier = generator.generateVerifier(salt, "HardwareScope".toByteArray(Charsets.UTF_8), pin.toByteArray(Charsets.US_ASCII))
        init(group, verifier, SHA256Digest(), SecureRandom())
    }
    private val public = srp.generateServerCredentials()

    override fun request(method: String, path: String, body: JSONObject?): JSONObject {
        require(isPairingRequest(method, path))
        requests++
        if (path == "/pair/start") {
            assertEquals(0, body!!.length())
            return JSONObject().put("sessionId", session).put("salt", salt.joinToString("") { "%02x".format(it) }).put("b", if (badPublic) "0" else public.toString(16)).put("expiresAt", Instant.now().plusSeconds(60).toString())
        }
        assertEquals(session, body!!.getString("sessionId"))
        srp.calculateSecret(BigInteger(body.getString("a"), 16))
        check(srp.verifyClientEvidenceMessage(BigInteger(body.getString("m1"), 16))) { "PIN 不正确" }
        authenticated = true
        val m2 = srp.calculateServerEvidenceMessage()
        val key = BigIntegers.asUnsignedByteArray(32, srp.calculateSessionKey())
        val nonce = ByteArray(12).also { SecureRandom().nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
            updateAAD(("HardwareScope.Pair.v1|" + session).toByteArray(Charsets.UTF_8))
        }
        val ciphertext = cipher.doFinal(JSONObject().put("token", token).put("fingerprint", fingerprint).put("product", "HardwareScope").toString().toByteArray(Charsets.UTF_8))
        if (badTag) ciphertext[ciphertext.lastIndex] = (ciphertext.last().toInt() xor 1).toByte()
        if (changedCertificate) peerFingerprint = "c3".repeat(32)
        return JSONObject().put("m2", if (badProof) "0" else m2.toString(16)).put("nonce", nonce.joinToString("") { "%02x".format(it) }).put("ciphertext", Base64.getEncoder().encodeToString(ciphertext))
    }
    override fun close() {}
}
