import Foundation
import CryptoKit
import Security

public enum GoogleOAuthError: String, Error, LocalizedError, Sendable {
    case invalidConfiguration, invalidCallback, stateMismatch, accessDenied, expired, cancelled
    case busy, notConnected, reauthorizationRequired, transport, invalidResponse, storage, listener
    public var errorDescription: String? { "Google OAuth: \(rawValue)" }
}

public struct GoogleOAuthClientConfiguration: Sendable {
    public let clientID: String
    public let clientSecret: String?
    public static let scope = "https://www.googleapis.com/auth/drive.file"
    public static let authorizationEndpoint = URL(string: "https://accounts.google.com/o/oauth2/v2/auth")!
    public static let tokenEndpoint = URL(string: "https://oauth2.googleapis.com/token")!

    public init(clientID: String, clientSecret: String? = nil) throws {
        guard clientID.hasSuffix(".apps.googleusercontent.com"), clientID.count <= 1024,
              !clientID.unicodeScalars.contains(where: { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.controlCharacters.contains($0) }),
              clientID.count > ".apps.googleusercontent.com".count,
              (clientSecret?.utf8.count ?? 0) <= 4096 else { throw GoogleOAuthError.invalidConfiguration }
        self.clientID = clientID
        self.clientSecret = clientSecret
    }

    /// Endpoint and redirect fields in the downloaded file are intentionally ignored.
    public static func loadInstalledJSON(_ data: Data) throws -> Self {
        struct File: Decodable { let installed: Installed; struct Installed: Decodable { let client_id: String; let client_secret: String? } }
        guard data.count <= 64 * 1024, let file = try? JSONDecoder().decode(File.self, from: data) else {
            throw GoogleOAuthError.invalidConfiguration
        }
        return try Self(clientID: file.installed.client_id, clientSecret: file.installed.client_secret)
    }
}

public struct GoogleOAuthPKCE: Sendable {
    public let verifier: String
    public let state: String
    public var challenge: String { Self.base64URL(Data(SHA256.hash(data: Data(verifier.utf8)))) }
    public init() throws {
        func random() throws -> String {
            var bytes = [UInt8](repeating: 0, count: 32)
            guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { throw GoogleOAuthError.transport }
            return Self.base64URL(Data(bytes))
        }
        verifier = try random(); state = try random()
    }
    public static func challenge(for verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }
    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    public func authorizationURL(configuration: GoogleOAuthClientConfiguration, redirectURI: URL) -> URL {
        var url = URLComponents(url: GoogleOAuthClientConfiguration.authorizationEndpoint, resolvingAgainstBaseURL: false)!
        url.queryItems = ["client_id": configuration.clientID, "redirect_uri": redirectURI.absoluteString,
            "response_type": "code", "scope": GoogleOAuthClientConfiguration.scope, "state": state,
            "code_challenge": challenge, "code_challenge_method": "S256", "access_type": "offline", "prompt": "consent"].map { URLQueryItem(name: $0.key, value: $0.value) }
        return url.url!
    }
    public func code(from target: String) throws -> String {
        guard target.utf8.count <= 8192, target.hasPrefix("/oauth/callback?"),
              let parts = URLComponents(string: target), parts.path == "/oauth/callback",
              parts.fragment == nil, let items = parts.queryItems,
              items.filter({ $0.name == "state" }).count == 1,
              let received = items.first(where: { $0.name == "state" })?.value else { throw GoogleOAuthError.invalidCallback }
        // Hashes have fixed lengths, so comparison does not expose a matching prefix.
        let a = Array(SHA256.hash(data: Data(received.utf8))), b = Array(SHA256.hash(data: Data(state.utf8)))
        guard zip(a, b).reduce(UInt8(0), { $0 | ($1.0 ^ $1.1) }) == 0 else { throw GoogleOAuthError.stateMismatch }
        let errors = items.filter { $0.name == "error" }, codes = items.filter { $0.name == "code" }
        if errors.count == 1, codes.isEmpty { throw GoogleOAuthError.accessDenied }
        guard errors.isEmpty, codes.count == 1, let code = codes[0].value, !code.isEmpty, code.utf8.count <= 4096 else { throw GoogleOAuthError.invalidCallback }
        return code
    }
}

public protocol GoogleOAuthTokenVault: Sendable {
    func loadRefreshToken() throws -> String?
    func saveRefreshToken(_ token: String) throws
    func deleteRefreshToken() throws
}

public struct GoogleOAuthHTTPResponse: Sendable {
    public let statusCode: Int
    public let data: Data
    public init(statusCode: Int, data: Data) { self.statusCode = statusCode; self.data = data }
}
public protocol GoogleOAuthHTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> GoogleOAuthHTTPResponse
}

private final class GoogleOAuthRedirectPolicy: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) { completionHandler(nil) }
}
public final class GoogleOAuthURLSessionTransport: GoogleOAuthHTTPTransport, Sendable {
    private let session: URLSession
    public init() {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.urlCache = nil; config.httpShouldSetCookies = false
        config.timeoutIntervalForRequest = 30; config.timeoutIntervalForResource = 45
        session = URLSession(configuration: config, delegate: GoogleOAuthRedirectPolicy(), delegateQueue: nil)
    }
    public func send(_ request: URLRequest) async throws -> GoogleOAuthHTTPResponse {
        guard request.url == GoogleOAuthClientConfiguration.tokenEndpoint, request.httpMethod == "POST" else { throw GoogleOAuthError.transport }
        do {
            let (bytes, response) = try await session.bytes(for: request)
            guard let response = response as? HTTPURLResponse else { throw GoogleOAuthError.invalidResponse }
            var data = Data()
            for try await byte in bytes {
                guard data.count < 64 * 1024 else { bytes.task.cancel(); throw GoogleOAuthError.invalidResponse }
                data.append(byte)
            }
            return GoogleOAuthHTTPResponse(statusCode: response.statusCode, data: data)
        } catch is CancellationError { throw GoogleOAuthError.cancelled }
        catch let error as GoogleOAuthError { throw error }
        catch { throw GoogleOAuthError.transport }
    }
}

public actor GoogleOAuthClient {
    private let configuration: GoogleOAuthClientConfiguration
    private let vault: any GoogleOAuthTokenVault
    private let http: any GoogleOAuthHTTPTransport
    private var token: String?
    private var expiration = Date.distantPast
    private var busy = false
    private var generation: UInt64 = 0
    private var loopback: GoogleOAuthLoopback?
    private var activeRequest: Task<GoogleOAuthHTTPResponse, any Error>?

    public init(configuration: GoogleOAuthClientConfiguration, vault: any GoogleOAuthTokenVault,
                http: any GoogleOAuthHTTPTransport = GoogleOAuthURLSessionTransport()) {
        self.configuration = configuration; self.vault = vault; self.http = http
    }
    public func connect(timeout: TimeInterval = 180, openBrowser: @Sendable (URL) async throws -> Void) async throws {
        guard !busy else { throw GoogleOAuthError.busy }
        busy = true; defer { busy = false; loopback = nil }
        let epoch = generation
        let pkce = try GoogleOAuthPKCE()
        let receiver = GoogleOAuthLoopback(pkce: pkce)
        loopback = receiver
        do {
            let redirect = try await receiver.start(timeout: timeout)
            try await openBrowser(pkce.authorizationURL(configuration: configuration, redirectURI: redirect))
            let code = try await receiver.waitForCode()
            let result = try await exchange(["grant_type": "authorization_code", "code": code,
                "redirect_uri": redirect.absoluteString, "code_verifier": pkce.verifier])
            try ensureActive(epoch)
            guard let refresh = result.refresh_token, !refresh.isEmpty else { throw GoogleOAuthError.invalidResponse }
            try save(refresh)
            token = result.access_token; expiration = Date().addingTimeInterval(result.expires_in)
        } catch {
            await receiver.cancel()
            throw safe(error)
        }
    }
    public func accessToken() async throws -> String {
        try Task.checkCancellation()
        if let token, expiration.timeIntervalSinceNow > 60 { return token }
        guard !busy else { throw GoogleOAuthError.busy }
        busy = true; defer { busy = false }
        let epoch = generation
        let refresh: String?
        do { refresh = try vault.loadRefreshToken() } catch { throw GoogleOAuthError.storage }
        guard let refresh else { throw GoogleOAuthError.notConnected }
        do {
            let result = try await exchange(["grant_type": "refresh_token", "refresh_token": refresh])
            try ensureActive(epoch)
            if let replacement = result.refresh_token { try save(replacement) }
            token = result.access_token; expiration = Date().addingTimeInterval(result.expires_in)
            return result.access_token
        } catch {
            if generation == epoch, (error as? GoogleOAuthError) == .reauthorizationRequired {
                token = nil; expiration = .distantPast
                do { try vault.deleteRefreshToken() } catch { throw GoogleOAuthError.storage }
            }
            throw safe(error)
        }
    }
    public func cancel() async {
        generation &+= 1
        activeRequest?.cancel()
        await loopback?.cancel()
    }
    /// Local disconnect; intentionally does not revoke the account's remote grant.
    public func disconnect() throws {
        generation &+= 1; token = nil; expiration = .distantPast
        activeRequest?.cancel()
        if let receiver = loopback { Task { await receiver.cancel() } }
        do { try vault.deleteRefreshToken() } catch { throw GoogleOAuthError.storage }
    }
    private func ensureActive(_ epoch: UInt64) throws {
        guard epoch == generation, !Task.isCancelled else { throw GoogleOAuthError.cancelled }
    }
    private func save(_ refresh: String) throws {
        do { try vault.saveRefreshToken(refresh) } catch { throw GoogleOAuthError.storage }
    }
    private struct TokenResponse: Decodable {
        let access_token: String; let expires_in: TimeInterval; let token_type: String
        let refresh_token: String?; let scope: String?
    }
    private func exchange(_ fields: [String: String]) async throws -> TokenResponse {
        var fields = fields; fields["client_id"] = configuration.clientID
        if let secret = configuration.clientSecret { fields["client_secret"] = secret }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        let body = fields.sorted(by: { $0.key < $1.key }).map { key, value in
            "\(key.addingPercentEncoding(withAllowedCharacters: allowed)!)=\(value.addingPercentEncoding(withAllowedCharacters: allowed)!)"
        }.joined(separator: "&")
        var request = URLRequest(url: GoogleOAuthClientConfiguration.tokenEndpoint)
        request.httpMethod = "POST"; request.httpBody = Data(body.utf8)
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let response: GoogleOAuthHTTPResponse
        let http = self.http
        let operation = Task { try await http.send(request) }
        activeRequest = operation
        defer { activeRequest = nil }
        do {
            response = try await withTaskCancellationHandler {
                try await operation.value
            } onCancel: { operation.cancel() }
        } catch { throw safe(error) }
        try Task.checkCancellation()
        guard response.data.count <= 64 * 1024 else { throw GoogleOAuthError.invalidResponse }
        if response.statusCode != 200 {
            struct Failure: Decodable { let error: String }
            if let failure = try? JSONDecoder().decode(Failure.self, from: response.data), failure.error == "invalid_grant" {
                throw GoogleOAuthError.reauthorizationRequired
            }
            throw GoogleOAuthError.transport
        }
        guard let result = try? JSONDecoder().decode(TokenResponse.self, from: response.data),
              !result.access_token.isEmpty, result.access_token.utf8.count <= 16384,
              result.token_type.lowercased() == "bearer", result.expires_in.isFinite, result.expires_in > 0,
              result.expires_in <= 86400 * 7,
              result.refresh_token.map({ !$0.isEmpty && $0.utf8.count <= 16384 }) ?? true,
              result.scope.map({ Set($0.split(separator: " ").map(String.init)) == [GoogleOAuthClientConfiguration.scope] }) ?? true
        else { throw GoogleOAuthError.invalidResponse }
        return result
    }
    private func safe(_ error: any Error) -> GoogleOAuthError {
        if error is CancellationError || Task.isCancelled { return .cancelled }
        return error as? GoogleOAuthError ?? .transport
    }
}
