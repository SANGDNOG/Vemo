package com.vemo.android

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.vemo.android.core.*
import java.io.ByteArrayInputStream
import java.security.KeyStore
import java.security.MessageDigest
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

data class DavCredentials(val endpoint: String, val folder: String, val user: String, val password: String) {
    fun identity(): String = MessageDigest.getInstance("SHA-256").digest("$endpoint\n$folder\n$user".toByteArray()).joinToString("") { "%02x".format(it) }
}
/** Credentials are encrypted as one authenticated blob. Access tokens and passwords are never logged. */
class CredentialVault(context: Context) {
    private val prefs = context.getSharedPreferences("credentials", Context.MODE_PRIVATE)
    private val alias = "vemo.webdav.v1"
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(alias, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setRandomizedEncryptionRequired(true).build())
        return generator.generateKey()
    }
    @Synchronized fun save(credentials: DavCredentials) {
        DavConfig(credentials.endpoint, credentials.folder).validate()
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        cipher.updateAAD(alias.toByteArray())
        val ciphertext = cipher.doFinal(Json.bytes(credentials))
        val bytes = byteArrayOf(cipher.iv.size.toByte()) + cipher.iv + ciphertext
        if (!prefs.edit().putString("sealed", Base64.encodeToString(bytes, Base64.NO_WRAP)).commit()) throw java.io.IOException("Cannot save protected credentials")
    }
    @Synchronized fun load(): DavCredentials? {
        val encoded = prefs.getString("sealed", null) ?: return null
        try {
            val bytes = Base64.decode(encoded, Base64.NO_WRAP)
            if (bytes.isEmpty() || bytes[0].toInt() != 12 || bytes.size < 29) throw java.security.GeneralSecurityException()
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(1, 13)))
            cipher.updateAAD(alias.toByteArray())
            val obj = Json.parse(cipher.doFinal(bytes.copyOfRange(13, bytes.size)))
            return DavCredentials(Json.string(obj, "endpoint"), Json.string(obj, "folder"), Json.string(obj, "user"), Json.string(obj, "password"))
        } catch (_: Exception) { throw java.security.GeneralSecurityException("Protected credentials unavailable") }
    }
    fun configured(): Boolean = prefs.contains("sealed")
    @Synchronized fun clear() {
        if (!prefs.edit().clear().commit()) throw java.io.IOException("Cannot clear credentials")
        KeyStore.getInstance("AndroidKeyStore").apply { load(null); deleteEntry(alias) }
    }
}
