package io.github.hardwarescope.mobile

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecureStore(context: Context) {
    private val prefs = context.getSharedPreferences("connection", Context.MODE_PRIVATE)
    private val alias = "HardwareScope.connection.token"

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(alias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        }.generateKey()
    }

    fun save(config: ConnectionConfig) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val ciphertext = cipher.doFinal(config.token.toByteArray(Charsets.UTF_8))
        prefs.edit().putString("mode", config.mode).putString("address", config.address).putString("fingerprint", config.fingerprint)
            .putString("iv", Base64.encodeToString(cipher.iv, Base64.NO_WRAP)).putString("token", Base64.encodeToString(ciphertext, Base64.NO_WRAP)).apply()
    }

    fun load(): ConnectionConfig? {
        val ciphertext = prefs.getString("token", null) ?: return null
        val iv = prefs.getString("iv", null) ?: return null
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, Base64.decode(iv, Base64.NO_WRAP))) }
        return ConnectionConfig(prefs.getString("mode", "HTTPS")!!, prefs.getString("address", "")!!, prefs.getString("fingerprint", "")!!,
            String(cipher.doFinal(Base64.decode(ciphertext, Base64.NO_WRAP)), Charsets.UTF_8))
    }

    fun clear() { prefs.edit().clear().apply() }
}
