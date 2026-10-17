import Foundation

private func check(_ condition: Bool, _ label: String) throws {
    guard condition else { throw CheckFailure.failed(label) }
    print("PASS: \(label)")
}
private enum CheckFailure: Error { case failed(String) }
private func rejects(_ expected: GoogleOAuthError, _ action: () async throws -> Void) async throws {
    do { try await action() } catch let error as GoogleOAuthError {
        try check(error == expected, "reject \(expected.rawValue)"); return
    }
    throw CheckFailure.failed("expected \(expected.rawValue)")
}
private final class MemoryVault: GoogleOAuthTokenVault, @unchecked Sendable {
    private let lock = NSLock()
    private var token: String?
    init(_ token: String? = nil) { self.token = token }
    func loadRefreshToken() throws -> String? { lock.withLock { token } }
    func saveRefreshToken(_ token: String) throws { lock.withLock { self.token = token } }
    func deleteRefreshToken() throws { lock.withLock { token = nil } }
}
private actor MockHTTP: GoogleOAuthHTTPTransport {
    var requests: [URLRequest] = []
    let status: Int
    let body: String
    init(status: Int = 200, body: String = #"{"access_token":"synthetic-access","refresh_token":"synthetic-refresh","expires_in":3600,"token_type":"Bearer"}"#) {
        self.status = status; self.body = body
    }
    func send(_ request: URLRequest) async throws -> GoogleOAuthHTTPResponse {
        requests.append(request)
        return .init(statusCode: status, data: Data(body.utf8))
    }
    func captured() -> [URLRequest] { requests }
}


private actor SlowHTTP: GoogleOAuthHTTPTransport {
    private var started = false
    func send(_ request: URLRequest) async throws -> GoogleOAuthHTTPResponse {
        started = true
        try await Task.sleep(for: .seconds(30))
        return .init(statusCode: 500, data: Data())
    }
    func hasStarted() -> Bool { started }
}
private struct FailingVault: GoogleOAuthTokenVault {
    func loadRefreshToken() throws -> String? { throw CheckFailure.failed("sensitive vault error") }
    func saveRefreshToken(_ token: String) throws { throw CheckFailure.failed("sensitive vault error") }
    func deleteRefreshToken() throws { throw CheckFailure.failed("sensitive vault error") }
}

@main struct GoogleOAuthChecks {
    static func main() async {
        do { try await run(); print("All Google OAuth checks passed") }
        catch { print("FAIL: \(error)"); exit(1) }
    }
    static func run() async throws {
        let config = try GoogleOAuthClientConfiguration.loadInstalledJSON(Data(#"{"installed":{"client_id":"synthetic.apps.googleusercontent.com","client_secret":"synthetic-secret","token_uri":"https://evil.invalid/token"}}"#.utf8))
        try check(config.clientID == "synthetic.apps.googleusercontent.com", "installed JSON loads")
        do {
            try await rejects(.invalidConfiguration) { _ = try GoogleOAuthClientConfiguration.loadInstalledJSON(Data(#"{"web":{"client_id":"x"}}"#.utf8)) }
        }
        try await rejects(.invalidConfiguration) { _ = try GoogleOAuthClientConfiguration(clientID: "evil.invalid") }
        try check(GoogleOAuthPKCE.challenge(for: "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "RFC7636 S256 vector")
        let pkce = try GoogleOAuthPKCE(), other = try GoogleOAuthPKCE()
        try check(pkce.state != other.state && pkce.verifier != other.verifier && pkce.verifier.count == 43, "independent secure random state and verifier")
        let url = pkce.authorizationURL(configuration: config, redirectURI: URL(string: "http://127.0.0.1:12345/oauth/callback")!)
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!
        try check(url.host == "accounts.google.com" && items.first(where: { $0.name == "scope" })?.value == GoogleOAuthClientConfiguration.scope, "fixed endpoint and drive.file only")
        try check(items.first(where: { $0.name == "code_challenge_method" })?.value == "S256", "S256 request")
        try check(try pkce.code(from: "/oauth/callback?state=\(pkce.state)&code=synthetic-code") == "synthetic-code", "valid callback")
        try await rejects(.stateMismatch) { _ = try pkce.code(from: "/oauth/callback?state=wrong&code=secret") }
        try await rejects(.invalidCallback) { _ = try pkce.code(from: "/oauth/callback?state=\(pkce.state)&code=a&code=b") }
        try await rejects(.invalidCallback) { _ = try pkce.code(from: "/oauth/callback?state=\(pkce.state)&state=\(pkce.state)&code=a") }
        try await rejects(.accessDenied) { _ = try pkce.code(from: "/oauth/callback?state=\(pkce.state)&error=access_denied") }
        try await rejects(.invalidCallback) { _ = try pkce.code(from: "/oauth/callback?state=\(pkce.state)&error=access_denied&code=a") }
        try await rejects(.invalidCallback) { _ = try pkce.code(from: "/oauth/callback?state=\(pkce.state)&code=a#fragment") }

        let transport = GoogleOAuthURLSessionTransport()
        try await rejects(.transport) { _ = try await transport.send(URLRequest(url: URL(string: "https://evil.invalid/token")!)) }
        let receiver = GoogleOAuthLoopback(pkce: pkce)
        let redirect = try await receiver.start(timeout: 2)
        try check(redirect.host == "127.0.0.1" && redirect.port != nil && redirect.port != 0, "ephemeral IPv4 loopback")
        var callback = URLComponents(url: redirect, resolvingAgainstBaseURL: false)!
        callback.queryItems = [.init(name: "state", value: pkce.state), .init(name: "code", value: "synthetic-code")]
        let (_, response) = try await URLSession.shared.data(from: callback.url!)
        try check((response as? HTTPURLResponse)?.statusCode == 200, "generic callback HTTP response")
        try check(try await receiver.waitForCode() == "synthetic-code", "loopback delivers callback once")
        try await rejects(.invalidCallback) { _ = try await receiver.waitForCode() }
        let preCancelled = GoogleOAuthLoopback(pkce: pkce)
        await preCancelled.cancel()
        try await rejects(.cancelled) { _ = try await preCancelled.start(timeout: 2) }
        let mismatch = GoogleOAuthLoopback(pkce: pkce)
        let mismatchURL = try await mismatch.start(timeout: 2)
        var badCallback = URLComponents(url: mismatchURL, resolvingAgainstBaseURL: false)!
        badCallback.queryItems = [.init(name: "state", value: "wrong"), .init(name: "code", value: "synthetic")]
        _ = try await URLSession.shared.data(from: badCallback.url!)
        try await rejects(.stateMismatch) { _ = try await mismatch.waitForCode() }
        let expired = GoogleOAuthLoopback(pkce: pkce)
        _ = try await expired.start(timeout: 0.05)
        try await rejects(.expired) { _ = try await expired.waitForCode() }
        let cancelled = GoogleOAuthLoopback(pkce: pkce)
        _ = try await cancelled.start(timeout: 2)
        await cancelled.cancel()
        try await rejects(.cancelled) { _ = try await cancelled.waitForCode() }
        let taskCancelled = GoogleOAuthLoopback(pkce: pkce)
        _ = try await taskCancelled.start(timeout: 2)
        let waiting = Task { try await taskCancelled.waitForCode() }
        waiting.cancel()
        try await rejects(.cancelled) { _ = try await waiting.value }

        let vault = MemoryVault(), http = MockHTTP()
        let client = GoogleOAuthClient(configuration: config, vault: vault, http: http)
        try await client.connect(timeout: 2) { authorizationURL in
            let parameters = URLComponents(url: authorizationURL, resolvingAgainstBaseURL: false)!.queryItems!
            let redirect = parameters.first(where: { $0.name == "redirect_uri" })!.value!
            let state = parameters.first(where: { $0.name == "state" })!.value!
            var callback = URLComponents(string: redirect)!
            callback.queryItems = [.init(name: "state", value: state), .init(name: "code", value: "synthetic+code&")]
            _ = try await URLSession.shared.data(from: callback.url!)
        }
        try check(try vault.loadRefreshToken() == "synthetic-refresh", "connect stores refresh in injected vault")
        try check(try await client.accessToken() == "synthetic-access", "connect caches access token")
        let requests = await http.captured()
        let form = String(data: requests[0].httpBody!, encoding: .utf8)!
        try check(requests.count == 1 && requests[0].url == GoogleOAuthClientConfiguration.tokenEndpoint, "token exchange fixed endpoint")
        try check(form.contains("code=synthetic%2Bcode%26") && form.contains("code_verifier=") && form.contains("grant_type=authorization_code"), "encoded code and PKCE exchange")
        try await client.disconnect()
        try check(try vault.loadRefreshToken() == nil, "local disconnect clears refresh")
        try await rejects(.notConnected) { _ = try await client.accessToken() }

        let refreshVault = MemoryVault("old-refresh"), refreshHTTP = MockHTTP()
        let refreshClient = GoogleOAuthClient(configuration: config, vault: refreshVault, http: refreshHTTP)
        _ = try await refreshClient.accessToken()
        try check(try refreshVault.loadRefreshToken() == "synthetic-refresh", "refresh token rotation persisted")
        let refreshRequests = await refreshHTTP.captured()
        try check(String(data: refreshRequests[0].httpBody!, encoding: .utf8)!.contains("grant_type=refresh_token"), "refresh grant")
        let invalidVault = MemoryVault("expired-refresh")
        let invalidClient = GoogleOAuthClient(configuration: config, vault: invalidVault, http: MockHTTP(status: 400, body: #"{"error":"invalid_grant","error_description":"sensitive diagnostic"}"#))
        try await rejects(.reauthorizationRequired) { _ = try await invalidClient.accessToken() }
        try check(try invalidVault.loadRefreshToken() == nil, "invalid grant clears unusable credential")
        let denied = GoogleOAuthClient(configuration: config, vault: MemoryVault("refresh"), http: MockHTTP(status: 403, body: "SECRET-TOKEN"))
        try await rejects(.transport) { _ = try await denied.accessToken() }
        let broad = GoogleOAuthClient(configuration: config, vault: MemoryVault("refresh"), http: MockHTTP(body: #"{"access_token":"a","expires_in":3600,"token_type":"Bearer","scope":"https://www.googleapis.com/auth/drive"}"#))
        try await rejects(.invalidResponse) { _ = try await broad.accessToken() }
        let stableVault = MemoryVault("keep-refresh")
        let noRotation = GoogleOAuthClient(configuration: config, vault: stableVault, http: MockHTTP(body: #"{"access_token":"a","expires_in":3600,"token_type":"Bearer"}"#))
        _ = try await noRotation.accessToken()
        try check(try stableVault.loadRefreshToken() == "keep-refresh", "refresh without rotation preserves credential")
        let badJSON = GoogleOAuthClient(configuration: config, vault: MemoryVault("r"), http: MockHTTP(body: "sensitive invalid JSON"))
        try await rejects(.invalidResponse) { _ = try await badJSON.accessToken() }
        let failingStorage = GoogleOAuthClient(configuration: config, vault: FailingVault(), http: http)
        try await rejects(.storage) { _ = try await failingStorage.accessToken() }
        let slow = SlowHTTP(), cancelledVault = MemoryVault("keep-refresh")
        let cancelClient = GoogleOAuthClient(configuration: config, vault: cancelledVault, http: slow)
        let refreshing = Task { try await cancelClient.accessToken() }
        while !(await slow.hasStarted()) { await Task.yield() }
        try await rejects(.busy) { _ = try await cancelClient.accessToken() }
        await cancelClient.cancel()
        try await rejects(.cancelled) { _ = try await refreshing.value }
        try check(try cancelledVault.loadRefreshToken() == "keep-refresh", "cancel preserves stored credential")
        try check(!GoogleOAuthError.transport.localizedDescription.contains("SECRET"), "diagnostics contain stable codes only")
    }
}
