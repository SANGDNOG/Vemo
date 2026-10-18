import Foundation

@main struct Checks {
    @MainActor static func main() async {
        var assertions = 0, failures = 0
        func check(_ value: Bool, _ label: String) {
            assertions += 1
            if !value { failures += 1; print("FAIL: " + label) }
        }
        let epoch = Date(timeIntervalSince1970: 1_800_000_000)
        func time(_ seconds: Double) -> Date { epoch.addingTimeInterval(seconds) }
        func model() -> GoogleConnectionModel {
            Probe.reset(); Probe.hasConfig = true; Probe.journal = true; Probe.hasToken = true
            return GoogleConnectionModel(root: URL(fileURLWithPath: "/synthetic-unused-state"))
        }
        func noInteraction(_ label: String) {
            check(Probe.browserOpens == 0 && Probe.oauthConnects == 0 && Probe.recoveries == 0
                  && Probe.roots == 0 && Probe.disconnects == 0 && Probe.saves == 0 && Probe.panel == nil,
                  label + ": no interactive authorization, recovery, root creation or config mutation")
            check(Probe.journal && Probe.hasToken, label + ": binding/token fixture retained")
        }
        // Error injected at the actual await boundary, never into the retry classifier.
        let transient: [(String, any Error, Bool)] = [
            ("HTTP429", GoogleDriveError.http(429), false),
            ("HTTP500", GoogleDriveError.http(500), false),
            ("HTTP503", GoogleDriveError.http(503), false),
            ("HTTP599", GoogleDriveError.http(599), false),
            ("OAuth.transport", GoogleOAuthError.transport, true),
            ("URL.timedOut", URLError(.timedOut), false),
            ("URL.cannotFindHost", URLError(.cannotFindHost), false),
            ("URL.cannotConnectToHost", URLError(.cannotConnectToHost), false),
            ("URL.dnsLookupFailed", URLError(.dnsLookupFailed), false),
            ("URL.networkConnectionLost", URLError(.networkConnectionLost), false),
            ("URL.notConnectedToInternet", URLError(.notConnectedToInternet), false)
        ]
        for (label, error, atToken) in transient {
            let m = model()
            if atToken { Probe.tokenError = error } else { Probe.prepareError = error }
            await m.restoreIfNeeded(now: time(0))
            check(!m.isConnected && !m.isBusy, label + ": startup failure settles disconnected")
            let first = (Probe.restores, Probe.tokens, Probe.prepares)
            Probe.tokenError = nil; Probe.prepareError = nil
            for second in [0.0, 1, 29.999] { await m.restoreIfNeeded(now: time(second)) }
            check(Probe.restores == first.0 && Probe.tokens == first.1 && Probe.prepares == first.2,
                  label + ": no retry before 30-second deadline")
            await m.restoreIfNeeded(now: time(30))
            check(m.isConnected && !m.isBusy && Probe.restores == first.0 + 1,
                  label + ": recovers on subsequent restore at 30 seconds")
            _ = await m.synchronize(imports: ImportBroker(), jobs: { [] })
            check(Probe.pulls == 1, label + ": restored connection can synchronize")
            let settled = Probe.restores
            await m.restoreIfNeeded(now: time(3600))
            check(Probe.restores == settled, label + ": success does not repeat restore")
            noInteraction(label)
            await m.shutdown()
        }
        // Backoff must be measured from each failed attempt, not the first startup only.
        let repeated = model(); Probe.prepareError = GoogleDriveError.http(503)
        await repeated.restoreIfNeeded(now: time(0))
        await repeated.restoreIfNeeded(now: time(30))
        check(Probe.prepares == 2 && !repeated.isConnected, "repeated503: second attempt occurs at deadline")
        Probe.prepareError = nil
        await repeated.restoreIfNeeded(now: time(59.999))
        check(Probe.prepares == 2, "repeated503: fresh 30-second backoff after second failure")
        await repeated.restoreIfNeeded(now: time(60))
        check(Probe.prepares == 3 && repeated.isConnected, "repeated503: recovery at second deadline")
        noInteraction("repeated503"); await repeated.shutdown()

        let terminal: [(String, any Error, Bool)] = [
            ("HTTP400", GoogleDriveError.http(400), false),
            ("HTTP401", GoogleDriveError.http(401), false),
            ("HTTP403", GoogleDriveError.http(403), false),
            ("HTTP404", GoogleDriveError.http(404), false),
            ("HTTP499", GoogleDriveError.http(499), false),
            ("HTTP600", GoogleDriveError.http(600), false),
            ("Drive.accountMismatch", GoogleDriveError.accountMismatch, false),
            ("Drive.conflict", GoogleDriveError.conflict, false),
            ("Drive.invalidMetadata", GoogleDriveError.invalidMetadata, false),
            ("OAuth.reauthorizationRequired", GoogleOAuthError.reauthorizationRequired, true),
            ("OAuth.notConnected", GoogleOAuthError.notConnected, true),
            ("OAuth.accessDenied", GoogleOAuthError.accessDenied, true),
            ("OAuth.storage", GoogleOAuthError.storage, true),
            ("OAuth.cancelled", GoogleOAuthError.cancelled, true),
            ("CancellationError", CancellationError(), false),
            ("URL.cancelled", URLError(.cancelled), false),
            ("URL.badURL", URLError(.badURL), false),
            ("URL.serverCertificateUntrusted", URLError(.serverCertificateUntrusted), false),
            ("unclassifiedError", NSError(domain: "synthetic-unclassified", code: 503), false)
        ]
        for (label, error, atToken) in terminal {
            let m = model()
            if atToken { Probe.tokenError = error } else { Probe.prepareError = error }
            await m.restoreIfNeeded(now: time(0))
            check(!m.isConnected && !m.isBusy, label + ": initial failure observed")
            let first = (Probe.restores, Probe.tokens, Probe.prepares, Probe.vaultReads)
            Probe.tokenError = nil; Probe.prepareError = nil
            for second in [30.0, 60, 3600] { await m.restoreIfNeeded(now: time(second)) }
            check(!m.isConnected && Probe.restores == first.0 && Probe.tokens == first.1
                  && Probe.prepares == first.2 && Probe.vaultReads == first.3,
                  label + ": no automatic retry after underlying error clears")
            noInteraction(label); await m.shutdown()
        }
        let storage = model(); Probe.storageFailure = true
        await storage.restoreIfNeeded(now: time(0))
        check(storage.needsKeychainRecovery && Probe.vaultReads == 1 && Probe.tokens == 0,
              "Keychain read-denied: explicit recovery gate, no token request")
        Probe.storageFailure = false
        for second in [30.0, 60, 3600] { await storage.restoreIfNeeded(now: time(second)) }
        check(!storage.isConnected && storage.needsKeychainRecovery && Probe.vaultReads == 1,
              "Keychain read-denied: no retry even when storage becomes readable")
        noInteraction("Keychain read-denied"); await storage.shutdown()

        let binding = model(); Probe.wrongProcessor = true
        await binding.restoreIfNeeded(now: time(0))
        check(!binding.isConnected && Probe.restores == 1 && Probe.tokens == 0, "processor binding mismatch observed")
        Probe.wrongProcessor = false
        await binding.restoreIfNeeded(now: time(3600))
        check(!binding.isConnected && Probe.restores == 1 && Probe.tokens == 0, "binding mismatch does not retry")
        noInteraction("binding mismatch"); await binding.shutdown()

        // Cancel the actual task, then throw a wrapped transport error at its token await.
        let cancelled = model(); Probe.cancelTaskOnToken = true; Probe.tokenError = GoogleOAuthError.transport
        let task = Task { @MainActor in await cancelled.restoreIfNeeded(now: time(0)) }
        await task.value
        let attempts = Probe.tokens
        Probe.cancelTaskOnToken = false; Probe.tokenError = nil
        await cancelled.restoreIfNeeded(now: time(3600))
        check(!cancelled.isConnected && attempts == 1 && Probe.tokens == attempts,
              "cancelled task wrapping OAuth.transport does not schedule retry")
        noInteraction("wrapped cancellation"); await cancelled.shutdown()

        // Only accepted explicit operations clear pending restore. Guarded idle calls are no-ops.
        let idle = model(); Probe.prepareError = GoogleDriveError.http(503)
        await idle.restoreIfNeeded(now: time(0))
        idle.cancel(); idle.recoverKeychainAccess()
        check(Probe.cancels == 0 && Probe.recoveries == 0, "idle cancel/unavailable recovery are guarded no-ops")
        Probe.prepareError = nil
        await idle.restoreIfNeeded(now: time(30))
        check(idle.isConnected && Probe.prepares == 2, "guarded no-ops retain pending transient retry")
        await idle.shutdown()

        // Cancel a busy restoration whose await later throws a transport error.
        let busy = model(), prepareGate = Gate()
        Probe.prepareGate = prepareGate; Probe.prepareError = GoogleOAuthError.transport
        let busyTask = Task { @MainActor in await busy.restoreIfNeeded(now: time(0)) }
        for _ in 0..<1000 { if prepareGate.entered { break }; await Task.yield() }
        check(prepareGate.entered && busy.isBusy, "busy cancel: restoration entered gated prepare")
        busy.cancel(); prepareGate.release(); await busyTask.value
        Probe.prepareGate = nil; Probe.prepareError = nil
        await busy.restoreIfNeeded(now: time(3600))
        check(!busy.isConnected && !busy.isBusy && Probe.prepares == 1 && Probe.cancels == 1,
              "busy cancel: wrapped transport cannot restore or schedule stale retry")
        await busy.shutdown()

        for action in ["connect", "disconnect", "shutdown"] {
            let m = model(); Probe.prepareError = GoogleDriveError.http(503)
            await m.restoreIfNeeded(now: time(0))
            switch action {
            case "connect": m.connect()
            case "recover": m.recoverKeychainAccess()
            case "disconnect": m.disconnect()
            case "cancel": m.cancel()
            default: await m.shutdown()
            }
            for _ in 0..<1000 { if !m.isBusy { break }; await Task.yield() }
            check(!m.isBusy, action + ": explicit operation drained")
            let before = (Probe.restores, Probe.prepares, Probe.tokens)
            Probe.prepareError = nil
            await m.restoreIfNeeded(now: time(3600))
            check(Probe.restores == before.0 && Probe.prepares == before.1 && Probe.tokens == before.2 && !m.isConnected,
                  action + ": pending retry cleared; no stale reconnect")
            await m.shutdown()
        }
        print("ASSERTIONS: \(assertions); FAILURES: \(failures)")
        exit(failures == 0 ? 0 : 1)
    }
}
