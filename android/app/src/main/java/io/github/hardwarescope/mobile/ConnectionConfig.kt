package io.github.hardwarescope.mobile

import java.net.URI

data class ConnectionConfig(val mode: String, val address: String, val fingerprint: String, val token: String) {
    fun validated(): ConnectionConfig {
        require(token.isNotBlank() && token.length <= 512 && !token.contains('\r') && !token.contains('\n')) { "请填写电脑端显示的访问密钥。" }
        if (mode == "Bluetooth") {
            require(address.matches(Regex("[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}"))) { "请先选择已配对的电脑。" }
            return copy(address = address.uppercase())
        }
        require(mode == "HTTPS") { "连接方式无效。" }
        val uri = try { URI(address.trim()) } catch (_: Exception) { throw IllegalArgumentException("请输入完整的 HTTPS 地址。") }
        require(uri.scheme.equals("https", true) && !uri.host.isNullOrBlank() && uri.userInfo == null && uri.query == null && uri.fragment == null && (uri.path.isNullOrEmpty() || uri.path == "/") && (uri.port == -1 || uri.port in 1..65535)) { "地址格式应为 https://电脑IP:端口，不包含路径或账号。" }
        val pin = normalizeFingerprint(fingerprint)
        return copy(address = address.trim().trimEnd('/'), fingerprint = pin)
    }
}

fun normalizeFingerprint(value: String): String {
    val clean = value.replace(Regex("[\\s:-]"), "").uppercase()
    require(clean.matches(Regex("[A-F0-9]{64}"))) { "请填写电脑端显示的 64 位 SHA-256 证书指纹。" }
    return clean
}
