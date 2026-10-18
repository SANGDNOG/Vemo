package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.net.UnknownHostException

class SyncFeedbackTest {
    @Test fun failuresAreScopedAndRetryDoesNotPretendSuccess() {
        val state = SyncFeedback()
        state.begin(listOf("a", "b"))
        assertTrue(state.isAttempting("a")); assertFalse(state.isAttempting("c"))
        state.fail("a", SyncProblem.CONNECTION)
        state.complete("b")
        assertEquals(SyncProblem.CONNECTION, state.problem("a"))
        assertNull(state.problem("b")); assertNull(state.problem("c"))
        state.begin(listOf("a"))
        assertEquals(SyncProblem.CONNECTION, state.problem("a"))
        state.complete("a"); assertNull(state.problem("a"))
        state.finish(); assertFalse(state.isAttempting("a"))
    }
    @Test fun preflightFailureOnlyAffectsAttemptedRecordings() {
        val state = SyncFeedback()
        state.begin(listOf("a")); state.failAttempt(SyncProblem.AUTH)
        assertEquals(SyncProblem.AUTH, state.problem("a")); assertNull(state.problem("b"))
        state.begin(listOf("b")); state.complete("b")
        assertEquals(SyncProblem.AUTH, state.problem("a"))
    }
    @Test fun failureKindsDoNotGuessNetworkFromAnyIoError() {
        assertEquals(SyncProblem.CONNECTION, SyncProblem.from(UnknownHostException()))
        assertEquals(SyncProblem.AUTH, SyncProblem.from(AuthException()))
        assertEquals(SyncProblem.CONFLICT, SyncProblem.from(ConflictException()))
        assertEquals(SyncProblem.VALIDATION, SyncProblem.from(ProtocolException("invalid")))
        assertEquals(SyncProblem.OTHER, SyncProblem.from(IOException("disk")))
    }
}
