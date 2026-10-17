import Foundation
import Network

/// One-use IPv4 loopback listener. All mutable lifecycle state is actor-isolated.
public actor GoogleOAuthLoopback {
    private let pkce: GoogleOAuthPKCE
    private let queue = DispatchQueue(label: "com.vemo.oauth.loopback")
    private var listener: NWListener?
    private var connections: [UUID: NWConnection] = [:]
    private var buffers: [UUID: Data] = [:]
    private var ready: CheckedContinuation<URL, any Error>?
    private var waiter: CheckedContinuation<String, any Error>?
    private var outcome: Result<String, GoogleOAuthError>?
    private var deadline: Task<Void, Never>?
    private var redirect: URL?
    private var started = false
    private var consumed = false

    public init(pkce: GoogleOAuthPKCE) { self.pkce = pkce }

    public func start(timeout: TimeInterval = 180) async throws -> URL {
        guard !started else { throw GoogleOAuthError.busy }
        if let outcome { _ = try outcome.get(); throw GoogleOAuthError.cancelled }
        started = true
        guard timeout.isFinite, timeout > 0, timeout <= 600 else { throw GoogleOAuthError.invalidConfiguration }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        parameters.allowLocalEndpointReuse = false
        let listener: NWListener
        do { listener = try NWListener(using: parameters) } catch { throw GoogleOAuthError.listener }
        self.listener = listener
        listener.stateUpdateHandler = { [weak self] state in Task { await self?.stateChanged(state) } }
        listener.newConnectionHandler = { [weak self] connection in
            Task { if let self { await self.accept(connection) } else { connection.cancel() } }
        }
        deadline = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(timeout)) } catch { return }
            await self?.finish(.failure(.expired))
        }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                ready = continuation
                listener.start(queue: queue)
                if Task.isCancelled { finish(.failure(.cancelled)) }
            }
        } onCancel: { Task { await self.cancel() } }
    }
    public func waitForCode() async throws -> String {
        guard started else { throw GoogleOAuthError.listener }
        guard !consumed else { throw GoogleOAuthError.invalidCallback }
        consumed = true
        return try await withTaskCancellationHandler {
            if Task.isCancelled { finish(.failure(.cancelled)) }
            if let outcome { return try outcome.get() }
            guard waiter == nil else { throw GoogleOAuthError.busy }
            return try await withCheckedThrowingContinuation { waiter = $0 }
        } onCancel: { Task { await self.cancel() } }
    }
    public func cancel() { finish(.failure(.cancelled)) }

    private func stateChanged(_ state: NWListener.State) {
        guard outcome == nil else { return }
        switch state {
        case .ready:
            guard let port = listener?.port,
                  let url = URL(string: "http://127.0.0.1:\(port.rawValue)/oauth/callback") else {
                finish(.failure(.listener)); return
            }
            redirect = url; ready?.resume(returning: url); ready = nil
        case .failed: finish(.failure(.listener))
        case .cancelled: finish(.failure(.cancelled))
        default: break
        }
    }
    private func accept(_ connection: NWConnection) {
        guard outcome == nil, connections.count < 8 else { connection.cancel(); return }
        let id = UUID(); connections[id] = connection; buffers[id] = Data()
        connection.start(queue: queue)
        receive(connection, id: id)
    }
    private func receive(_ connection: NWConnection, id: UUID) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, complete, error in
            Task { await self?.received(data, complete: complete, failed: error != nil, id: id) }
        }
    }
    private func received(_ data: Data?, complete: Bool, failed: Bool, id: UUID) {
        guard outcome == nil, let connection = connections[id], var buffer = buffers[id] else { return }
        if let data { buffer.append(data) }
        guard buffer.count <= 8192 else { drop(id); return }
        buffers[id] = buffer
        guard let end = buffer.range(of: Data("\r\n\r\n".utf8)) else {
            if complete || failed { drop(id) } else { receive(connection, id: id) }
            return
        }
        guard end.upperBound == buffer.endIndex, let text = String(data: buffer, encoding: .utf8),
              let line = text.components(separatedBy: "\r\n").first else { drop(id); return }
        let pieces = line.split(separator: " ", omittingEmptySubsequences: false)
        let headers = text.components(separatedBy: "\r\n").dropFirst().filter { !$0.isEmpty }
        let hosts = headers.filter { $0.lowercased().hasPrefix("host:") }
        guard pieces.count == 3, pieces[0] == "GET", pieces[2] == "HTTP/1.1",
              hosts.count == 1, hosts[0].dropFirst(5).trimmingCharacters(in: .whitespaces) == "127.0.0.1:\(redirect?.port ?? 0)",
              !headers.contains(where: { $0.lowercased().hasPrefix("transfer-encoding:") || $0.lowercased().hasPrefix("content-length:") }) else {
            drop(id); return
        }
        let target = String(pieces[1])
        // Favicon requests and unrelated local probes do not consume the authorization attempt.
        guard target.hasPrefix("/oauth/callback?") else { drop(id); return }
        let result: Result<String, GoogleOAuthError>
        do { result = .success(try pkce.code(from: target)) }
        catch { result = .failure(error as? GoogleOAuthError ?? .invalidCallback) }
        let message = "Authorization received. You may close this window."
        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: \(message.utf8.count)\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\n\r\n\(message)"
        // Remove the responding connection before cleanup; close it only after the generic response is sent.
        connections.removeValue(forKey: id); buffers.removeValue(forKey: id)
        connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
        finish(result)
    }
    private func drop(_ id: UUID) {
        connections.removeValue(forKey: id)?.cancel(); buffers.removeValue(forKey: id)
    }
    private func finish(_ result: Result<String, GoogleOAuthError>) {
        guard outcome == nil else { return }
        outcome = result
        deadline?.cancel(); deadline = nil
        listener?.stateUpdateHandler = nil; listener?.newConnectionHandler = nil
        listener?.cancel(); listener = nil
        connections.values.forEach { $0.cancel() }; connections.removeAll(); buffers.removeAll()
        if let ready { ready.resume(throwing: result.failure ?? .cancelled); self.ready = nil }
        if let waiter { waiter.resume(with: result.mapError { $0 as any Error }); self.waiter = nil }
    }
}

private extension Result where Failure == GoogleOAuthError {
    var failure: GoogleOAuthError? { if case .failure(let error) = self { return error }; return nil }
}
