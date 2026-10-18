package com.vemo.android.core

/** Last observed failure in this process, separate from persisted human-readable status. */
enum class SyncProblem {
    CONNECTION, AUTH, CONFLICT, VALIDATION, OTHER;
    companion object {
        fun from(error: Exception): SyncProblem = when (error) {
            is AuthException, is java.security.GeneralSecurityException -> AUTH
            is ConflictException -> CONFLICT
            is ProtocolException -> VALIDATION
            is java.net.UnknownHostException, is java.net.ConnectException,
            is java.net.SocketTimeoutException, is javax.net.ssl.SSLException -> CONNECTION
            else -> OTHER
        }
    }
}

class SyncFeedback {
    private val failures = mutableMapOf<String, SyncProblem>()
    private var attempted = emptySet<String>()
    @Synchronized fun begin(ids: Collection<String>) { attempted = ids.toSet() }
    @Synchronized fun isAttempting(id: String): Boolean = id in attempted
    @Synchronized fun finish() { attempted = emptySet() }
    @Synchronized fun fail(id: String, problem: SyncProblem) { failures[id] = problem }
    @Synchronized fun failAttempt(problem: SyncProblem) { attempted.forEach { failures[it] = problem } }
    @Synchronized fun complete(id: String) { failures.remove(id) }
    @Synchronized fun problem(id: String): SyncProblem? = failures[id]
}
