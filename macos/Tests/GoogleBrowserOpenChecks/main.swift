import Foundation

private actor CallbackGate {
    var entered = false
    private var callback: CheckedContinuation<Void, Never>?
    func wait() async { entered = true; await withCheckedContinuation { callback = $0 } }
    func complete() { callback?.resume(); callback = nil }
}

private struct NoTokenVault: GoogleOAuthTokenVault {
    func loadRefreshToken() throws -> String? { nil }
    func saveRefreshToken(_ token: String) throws { fatalError("Unexpected token persistence") }
    func deleteRefreshToken() throws {}
}
private struct NoTokenHTTP: GoogleOAuthHTTPTransport {
    func send(_ request: URLRequest) async throws -> GoogleOAuthHTTPResponse { fatalError("Unexpected token request") }
}

@main struct BrowserOpenChecks {
    static func main() async throws {
        for cancel in [false, true] {
            let gate = CallbackGate(), pending = GoogleOAuthBrowserWait()
            let operation = Task {
                try await pending.wait(timeout: cancel ? 5 : 0.03) { await gate.wait() }
            }
            while !(await gate.entered) { await Task.yield() }
            if cancel { pending.cancel(); pending.cancel() }
            do { try await operation.value; fatalError("Expected bounded failure") }
            catch let error as GoogleOAuthError { precondition(error == (cancel ? .cancelled : .expired)) }
            // An eventual success must neither resume twice nor override timeout/cancellation.
            await gate.complete()
            pending.cancel()
            print("PASS: bounded \(cancel ? "cancel" : "timeout") and late completion")
        }
        let gate = CallbackGate()
        let client = GoogleOAuthClient(configuration: try GoogleOAuthClientConfiguration(clientID: "synthetic.apps.googleusercontent.com"), vault: NoTokenVault(), http: NoTokenHTTP())
        let connecting = Task { try await client.connect { _ in await gate.wait() } }
        while !(await gate.entered) { await Task.yield() }
        try await client.disconnect()
        do { try await connecting.value; fatalError("Expected disconnect cancellation") }
        catch let error as GoogleOAuthError { precondition(error == .cancelled) }
        await gate.complete()
        // A new connect must enter its browser request: the old busy state has drained.
        do { try await client.connect { _ in throw GoogleOAuthError.accessDenied }; fatalError("Expected denial") }
        catch let error as GoogleOAuthError { precondition(error == .accessDenied) }
        print("PASS: disconnect drains connection; late completion cannot exchange tokens; retry is available")
        let cancelled = GoogleOAuthBrowserWait()
        cancelled.cancel()
        do {
            try await cancelled.wait(timeout: 1) { fatalError("Cancelled browser request launched") }
            fatalError("Expected cancellation")
        } catch let error as GoogleOAuthError { precondition(error == .cancelled) }
        print("PASS: cancellation before waiter registration")
        let success = GoogleOAuthBrowserWait()
        try await success.wait(timeout: 1) {}
        success.cancel()
        print("PASS: immediate completion and late cancellation")
    }
}
