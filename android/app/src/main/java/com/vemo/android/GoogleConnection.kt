package com.vemo.android

import android.content.Context
import com.google.android.gms.auth.api.identity.AuthorizationRequest
import com.google.android.gms.auth.api.identity.ClearTokenRequest
import com.google.android.gms.auth.api.identity.Identity
import com.google.android.gms.common.api.Scope
import com.google.android.gms.tasks.Tasks
import com.vemo.android.core.*
import java.util.concurrent.TimeUnit

/** Only the immutable binding and pending Drive IDs are persisted. Tokens stay in memory/SDK. */
class GoogleConnection(private val context: Context) {
    private val prefs = context.getSharedPreferences("google_drive", Context.MODE_PRIVATE)
    @Volatile private var accessToken: String? = null
    @Volatile private var tokenAt = 0L
    @Volatile private var generation = 0L
    @Volatile private var cancelGeneration = 0L
    private val transport = SecureHttpTransport()
    fun request(): AuthorizationRequest = AuthorizationRequest.builder().setRequestedScopes(listOf(Scope(DRIVE_FILE_SCOPE))).build()
    fun binding(): DriveBinding? {
        val value = prefs.getString("binding", null) ?: return null
        val obj = Json.parse(value.toByteArray(Charsets.UTF_8))
        return DriveBinding(Json.string(obj, "accountId"), Json.string(obj, "rootId"), Json.string(obj, "processorId"))
    }
    fun configured(): Boolean = prefs.contains("binding")
    @Synchronized fun accept(token: String) {
        if (token.isBlank()) throw AuthException()
        accessToken = token; tokenAt = android.os.SystemClock.elapsedRealtime()
    }
    private fun token(): String {
        val now = android.os.SystemClock.elapsedRealtime()
        accessToken?.let { if (now - tokenAt < 45 * 60_000) return it }
        val version = generation
        val result = try { Tasks.await(Identity.getAuthorizationClient(context).authorize(request()), 60, TimeUnit.SECONDS) }
        catch (_: Exception) { throw AuthException() }
        // A background worker never launches an account/consent screen.
        if (result.hasResolution() || version != generation) throw AuthException()
        val value = result.accessToken ?: throw AuthException()
        accept(value)
        return value
    }
    fun api(): GoogleDriveApi {
        val version = cancelGeneration
        val access by lazy { token() }
        return GoogleDriveApi({ access }, transport) { version != cancelGeneration }
    }
    @Synchronized fun cancel() { cancelGeneration++; transport.cancel() }
    fun select(binding: DriveBinding) {
        val cloud = GoogleDriveCloud(api(), binding, reservations)
        if (SyncEngine(cloud).processor().id != binding.processorId) throw ProtocolException("Drive processor.json does not match root")
        if (!prefs.edit().putString("binding", String(Json.bytes(binding), Charsets.UTF_8)).commit()) throw java.io.IOException("Cannot save Drive binding")
    }
    fun cloud(binding: DriveBinding): GoogleDriveCloud = GoogleDriveCloud(api(), binding, reservations)
    val reservations = object : DriveReservations {
        override fun get(key: String): String? = prefs.getString("pending:$key", null)
        override fun save(key: String, id: String) {
            if (!prefs.edit().putString("pending:$key", id).commit()) throw java.io.IOException("Cannot persist Drive upload identity")
        }
    }
    @Synchronized fun invalidate() {
        generation++
        accessToken?.let { Identity.getAuthorizationClient(context).clearToken(ClearTokenRequest.builder().setToken(it).build()) }
        accessToken = null; tokenAt = 0
    }
    fun disconnect() {
        cancel(); invalidate()
        // Retain reservations so reconnecting the same binding safely recovers lost acknowledgements.
        if (!prefs.edit().remove("binding").commit()) throw java.io.IOException("Cannot disconnect Drive")
    }
}
