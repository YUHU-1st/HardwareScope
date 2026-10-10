package io.github.hardwarescope.mobile

import org.bouncycastle.crypto.agreement.srp.SRP6Client
import org.bouncycastle.crypto.agreement.srp.SRP6StandardGroups
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.util.BigIntegers
import org.json.JSONObject
import java.math.BigInteger
import java.security.SecureRandom
import java.time.Instant
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

interface PairingTransport : Transport {
    /** HTTPS certificate observed on the bootstrap connection, never supplied by discovery. */
    val peerFingerprint: String?
}

internal fun isPairingRequest(method: String, path: String) = method == "POST" && path in setOf("/pair/start", "/pair/finish")

class PinPairingClient {
    fun pair(target: PairingTarget, pin: String, transport: PairingTransport): ConnectionConfig {
        val validated = target.validated()
        require(pin.matches(Regex("[0-9]{6}"))) { "请输入电脑显示的六位 PIN，保留开头的 0。" }
        val challenge = transport.request("POST", "/pair/start", JSONObject())
        val session = challenge.getString("sessionId")
        require(session.matches(Regex("[a-fA-F0-9]{32}"))) { "电脑配对会话无效。" }
        require(Instant.parse(challenge.getString("expiresAt")).isAfter(Instant.now())) { "电脑 PIN 已过期，请刷新后重试。" }
        val salt = decodeHex(challenge.getString("salt"), 16)
        val group = SRP6StandardGroups.rfc5054_3072
        val serverPublic = unsignedHex(challenge.getString("b"), 768)
        require(serverPublic.signum() > 0 && serverPublic < group.n) { "电脑配对参数无效。" }
        val srp = SRP6Client().apply { init(group, SHA256Digest(), SecureRandom()) }
        val public = srp.generateClientCredentials(salt, "HardwareScope".toByteArray(Charsets.UTF_8), pin.toByteArray(Charsets.US_ASCII))
        srp.calculateSecret(serverPublic)
        val proof = srp.calculateClientEvidenceMessage()
        val envelope = transport.request("POST", "/pair/finish", JSONObject().put("sessionId", session).put("a", public.toString(16)).put("m1", proof.toString(16)))
        check(srp.verifyServerEvidenceMessage(unsignedHex(envelope.getString("m2"), 64))) { "PIN 不正确或电脑身份验证失败，请重试。" }
        val key = BigIntegers.asUnsignedByteArray(32, srp.calculateSessionKey())
        val nonce = decodeHex(envelope.getString("nonce"), 12)
        val encoded = envelope.getString("ciphertext")
        require(encoded.length <= 8192) { "电脑返回的连接凭据过长。" }
        val ciphertext = Base64.getDecoder().decode(encoded)
        require(ciphertext.size in 17..6144) { "电脑返回的连接凭据无效。" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
            updateAAD(("HardwareScope.Pair.v1|" + session).toByteArray(Charsets.UTF_8))
        }
        val data = try { cipher.doFinal(ciphertext) } finally { key.fill(0) }
        val credentials = try { JSONObject(String(data, Charsets.UTF_8)) } finally { data.fill(0) }
        require(credentials.getString("product") == "HardwareScope") { "电脑连接凭据的产品标识不正确。" }
        val fingerprint = normalizeFingerprint(credentials.getString("fingerprint"))
        if (validated.mode == "HTTPS") {
            check(transport.peerFingerprint?.let(::normalizeFingerprint) == fingerprint) { "配对证书发生变化，请在电脑端重新生成 PIN 后重试。" }
        }
        val token = credentials.getString("token")
        require(token.matches(Regex("[a-fA-F0-9]{64}"))) { "电脑返回的连接凭据无效。" }
        return ConnectionConfig(validated.mode, validated.address, fingerprint, token).validated()
    }
}

private fun unsignedHex(value: String, maxDigits: Int): BigInteger {
    require(value.length in 1..maxDigits && value.matches(Regex("[a-fA-F0-9]+"))) { "电脑配对参数无效。" }
    return BigInteger(value, 16)
}

private fun decodeHex(value: String, bytes: Int): ByteArray {
    require(value.length == bytes * 2 && value.matches(Regex("[a-fA-F0-9]+"))) { "电脑配对参数无效。" }
    return ByteArray(bytes) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}
