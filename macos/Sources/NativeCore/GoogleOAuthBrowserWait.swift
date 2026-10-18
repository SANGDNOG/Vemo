import Foundation

/// One-shot completion for an unstructured browser request. A system callback may never return,
/// so cancellation/deadline must release the caller without joining that request's task.
/// All mutable state is protected by lock; continuations are resumed outside the lock.
final class GoogleOAuthBrowserWait: @unchecked Sendable {
    private let lock = NSLock()
    private var outcome: Result<Void, any Error>?
    private var continuation: CheckedContinuation<Void, any Error>?

    func cancel() { finish(.failure(GoogleOAuthError.cancelled)) }

    func wait(timeout: TimeInterval, openBrowser: @escaping @Sendable () async throws -> Void) async throws {
        let request = Task {
            guard !lock.withLock({ outcome != nil }) else { return }
            do {
                try Task.checkCancellation()
                try await openBrowser()
                finish(.success(()))
            } catch { finish(.failure(error)) }
        }
        let deadline = Task {
            do { try await Task.sleep(for: .seconds(timeout)) } catch { return }
            finish(.failure(GoogleOAuthError.expired))
        }
        defer { request.cancel(); deadline.cancel() }
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await withCheckedThrowingContinuation { waiter in
                let result: Result<Void, any Error>? = lock.withLock {
                    if let outcome { return outcome }
                    continuation = waiter
                    return nil as Result<Void, any Error>?
                }
                if let result { waiter.resume(with: result) }
            }
            try Task.checkCancellation()
        } onCancel: { self.cancel() }
    }

    private func finish(_ result: Result<Void, any Error>) {
        let waiter = lock.withLock {
            guard outcome == nil else { return nil as CheckedContinuation<Void, any Error>? }
            outcome = result
            let waiter = continuation
            continuation = nil
            return waiter
        }
        waiter?.resume(with: result)
    }
}
