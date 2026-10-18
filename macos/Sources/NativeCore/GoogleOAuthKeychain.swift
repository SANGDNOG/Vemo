import Foundation
import Security
import LocalAuthentication

/// Supply a distinct account per OAuth client/binding. Never writes tokens to disk or defaults.
public struct GoogleOAuthKeychainVault: GoogleOAuthTokenVault {
    private let service: String
    private let account: String
    public init(service: String = "com.vemo.google-oauth", account: String) {
        self.service = service; self.account = account
    }
    private var query: [String: Any] {
        let context = LAContext()
        context.interactionNotAllowed = true
        return [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: account, kSecAttrSynchronizable as String: false,
         kSecUseAuthenticationContext as String: context]
    }
    /// Explicit foreground recovery only. The interactive context is never retained by this vault.
    /// Reads the existing item once; does not save/delete tokens or explicitly modify its ACL.
    public func recoverRefreshTokenAccess() async throws -> Bool {
        let prompt = GoogleOAuthRecoveryContext()
        let service = self.service, account = self.account
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            let worker = Task.detached {
                let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                    kSecAttrService as String: service, kSecAttrAccount as String: account,
                    kSecAttrSynchronizable as String: false, kSecUseAuthenticationContext as String: prompt.context,
                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
                var result: CFTypeRef?
                let status = SecItemCopyMatching(query as CFDictionary, &result)
                if status == errSecItemNotFound { return false }
                guard status == errSecSuccess, let data = result as? Data,
                      !data.isEmpty, data.count <= 16384, String(data: data, encoding: .utf8) != nil else {
                    throw GoogleOAuthError.storage
                }
                return true
            }
            let found = try await worker.value
            try Task.checkCancellation()
            return found
        } onCancel: { prompt.context.invalidate() }
    }

    /// Attribute-only, noninteractive migration guard. Never reads or copies another client's token.
    public static func hasAuthorization(accountPrefix: String) throws -> Bool {
        let context = LAContext()
        context.interactionNotAllowed = true
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.vemo.google-oauth", kSecAttrSynchronizable as String: false,
            kSecUseAuthenticationContext as String: context, kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitAll]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return false }
        guard status == errSecSuccess, let items = result as? [[String: Any]] else { throw GoogleOAuthError.storage }
        return items.contains { ($0[kSecAttrAccount as String] as? String)?.hasPrefix(accountPrefix) == true }
    }

    public func loadRefreshToken() throws -> String? {
        var query = query; query[kSecReturnData as String] = true; query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data,
              let token = String(data: data, encoding: .utf8), !token.isEmpty else { throw GoogleOAuthError.storage }
        return token
    }
    public func saveRefreshToken(_ token: String) throws {
        guard !token.isEmpty, token.utf8.count <= 16384 else { throw GoogleOAuthError.storage }
        let values: [String: Any] = [kSecValueData as String: Data(token.utf8),
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let status = SecItemUpdate(query as CFDictionary, values as CFDictionary)
        if status == errSecItemNotFound {
            var item = query; values.forEach { item[$0.key] = $0.value }
            guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw GoogleOAuthError.storage }
        } else if status != errSecSuccess { throw GoogleOAuthError.storage }
    }
    public func deleteRefreshToken() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw GoogleOAuthError.storage }
    }
}

// LAContext supports invalidating an outstanding evaluation from the cancellation handler.
private final class GoogleOAuthRecoveryContext: @unchecked Sendable {
    let context = LAContext()
}
