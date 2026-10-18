import Foundation
import AppKit
import UniformTypeIdentifiers

// Generated-source lifecycle fixture: synthetic I/O, job shape and policy boundaries.
// Not a production NativeCore integration test; no coordinator state is injected.
@MainActor final class Gate {
    var entered = false
    var sawCancellation = false
    private var continuation: CheckedContinuation<Void, Never>?
    func wait() async {
        entered = true
        await withCheckedContinuation { continuation = $0 }
        sawCancellation = Task.isCancelled
    }
    func release() { continuation?.resume(); continuation = nil }
}
@MainActor enum Probe {
    static var bundle = Bundle.main
    static let custom = Data(#"{"installed":{"client_id":"custom.apps.googleusercontent.com"}}"#.utf8)
    static var installedIDs: [String] = []
    static var savedConfiguration: Data?
    static var orphanToken = false
    static var hasConfig = false, journal = false, hasToken = false
    static var prepareError: (any Error)?, tokenError: (any Error)?
    static var vaultReads = 0, configReads = 0
    static var wrongProcessor = false
    static var cancelTaskOnToken = false
    static var browserOpens = 0, saves = 0, oauthConnects = 0, tokens = 0
    static var cancels = 0, disconnects = 0, restores = 0, prepares = 0, pulls = 0, roots = 0
    static var connectGate: Gate?, cancelGate: Gate?, pullGate: Gate?, recoveryGate: Gate?, prepareGate: Gate?
    static var tokenFailure = false
    static var storageFailure = false, recoveryDeclined = false
    static var recoveries = 0
    static var panel: ProbeOpenPanel?
    static func reset() {
        bundle = Bundle.main; installedIDs = []; savedConfiguration = nil; orphanToken = false
        prepareError = nil; tokenError = nil; vaultReads = 0; configReads = 0
        wrongProcessor = false; cancelTaskOnToken = false
        hasConfig = false; journal = false; hasToken = false; tokenFailure = false
        storageFailure = false; recoveryDeclined = false; recoveries = 0
        browserOpens = 0; saves = 0; oauthConnects = 0; tokens = 0; cancels = 0
        disconnects = 0; restores = 0; prepares = 0; pulls = 0; roots = 0
        connectGate = nil; cancelGate = nil; pullGate = nil; recoveryGate = nil; prepareGate = nil; panel = nil
    }
}
@MainActor final class ProbeOpenPanel {
    var allowedContentTypes: [UTType] = []
    var canChooseDirectories = false, allowsMultipleSelection = false
    var message = ""
    var url: URL? = URL(fileURLWithPath: "/synthetic/installed-client.json")
    private var completion: ((NSApplication.ModalResponse) -> Void)?
    func begin(_ callback: @escaping (NSApplication.ModalResponse) -> Void) { completion = callback; Probe.panel = self }
    func cancel(_ sender: Any?) { finish(.cancel) }
    func finish(_ response: NSApplication.ModalResponse) { let call = completion; completion = nil; call?(response) }
}
@MainActor struct GoogleClientConfigurationFile {
    let directory: URL
    func hasJournal() throws -> Bool { Probe.journal }
    func load() throws -> Data? { Probe.configReads += 1; return Probe.hasConfig ? (Probe.savedConfiguration ?? Probe.custom) : nil }
    static func readSelected(_ url: URL) throws -> Data { Probe.custom }
    func save(_ bytes: Data, replacingExisting: Bool = true) throws {
        if !replacingExisting && Probe.hasConfig { throw GoogleOAuthError.storage }
        Probe.saves += 1; Probe.hasConfig = true; Probe.savedConfiguration = bytes
    }
}
@MainActor struct GoogleOAuthKeychainVault {
    let account: String
    static func hasAuthorization(accountPrefix: String) throws -> Bool {
        if Probe.storageFailure { throw GoogleOAuthError.storage }
        return Probe.orphanToken
    }
    func loadRefreshToken() throws -> String? {
        Probe.vaultReads += 1
        if Probe.storageFailure { throw GoogleOAuthError.storage }
        return Probe.hasToken ? "synthetic-not-a-token" : nil
    }
    func recoverRefreshTokenAccess() async throws -> Bool {
        Probe.recoveries += 1
        if let gate = Probe.recoveryGate { await gate.wait() }
        try Task.checkCancellation()
        if Probe.recoveryDeclined { throw GoogleOAuthError.storage }
        Probe.storageFailure = false
        return Probe.hasToken
    }
}
public enum GoogleOAuthError: String, Error { case invalidConfiguration, invalidCallback, stateMismatch, accessDenied, expired, cancelled, busy, notConnected, reauthorizationRequired, transport, invalidResponse, storage, listener }
enum GoogleDriveError: Error, Equatable { case invalidResponse, limit, invalidMetadata, busy, staleJob, conflict, accountMismatch, notConnected, http(Int) }
struct BrokerFailure: Error { let code: String }
@MainActor final class GoogleOAuthClient {
    init(configuration: GoogleOAuthClientConfiguration, vault: GoogleOAuthKeychainVault) { Probe.installedIDs.append(configuration.clientID) }
    func connect(openBrowser: @Sendable (URL) async throws -> Void) async throws {
        Probe.oauthConnects += 1
        try await openBrowser(URL(string: "https://synthetic.invalid/never-opened")!)
        if let gate = Probe.connectGate { await gate.wait() }
        try Task.checkCancellation()
    }
    func accessToken() async throws -> String {
        Probe.tokens += 1
        if Probe.cancelTaskOnToken { withUnsafeCurrentTask { $0?.cancel() } }
        if let error = Probe.tokenError { throw error }
        if Probe.tokenFailure { throw GoogleOAuthError.notConnected }
        try Task.checkCancellation()
        return "synthetic-not-a-token"
    }
    func disconnect() async throws { Probe.disconnects += 1 }
    func cancel() async {
        Probe.cancels += 1
        Probe.connectGate?.release()
        if let gate = Probe.cancelGate { await gate.wait() }
    }
}
@MainActor struct GoogleDriveClient {
    init(accessToken: @escaping @Sendable () async throws -> String) {}
}
@MainActor struct Binding { var processorID: String { Probe.wrongProcessor ? "wrong-processor" : "synthetic-processor" } }
@MainActor final class GoogleDriveMirror {
    init(stateDirectory: URL, client: GoogleDriveClient) {}
    func restore() async throws -> Binding? { Probe.restores += 1; return Probe.journal ? Binding() : nil }
    func createRoot(displayName: String, processorID: String) async throws -> Binding {
        Probe.roots += 1; Probe.journal = true; return Binding()
    }
    func prepare(broker: MobileSyncBroker, displayName: String) async throws {
        Probe.prepares += 1
        if let gate = Probe.prepareGate { await gate.wait() }
        if let error = Probe.prepareError { throw error }
    }
    func pull() async throws {
        Probe.pulls += 1
        if let gate = Probe.pullGate { await gate.wait() }
        try Task.checkCancellation()
    }
    func pushResult(job: Job, broker: MobileSyncBroker, shouldPublish: @escaping @Sendable () async -> Bool) async throws {
        guard await shouldPublish() else { throw GoogleDriveError.staleJob }
    }
}
enum MobileSyncNamespace { case googleDrive }
struct Issue {}
struct Scan { let pending: [String] = []; let issues: [Issue] = [] }
struct Mapping { let suppressed: Bool; let memoUUID: String; let jobID: String?; let delivered: Bool }
struct Job: Sendable {
    let uuid: String; let id: String; let deleted_at: String?; let purge_pending: Bool?
    let status: String; let notes_status: String?; let wait_reason: String?; let recorded_at: String?
    let title: String; let recording_date: String?; let audio_path: String?; let artifact_path: String?; let notes_path: String?
}
// Pure production policy is extracted by run.py; jobs remain synthetic and absent in lifecycle cases.
struct ImportBroker {}
@MainActor final class MobileSyncBroker {
    init(root: URL, namespace: MobileSyncNamespace) {}
    func processorIdentity() async throws -> String { "synthetic-processor" }
    func scan(importBroker: ImportBroker) async throws -> Scan { Scan() }
    func mappings() async throws -> [Mapping] { [] }
    func bindJob(memoUUID: String, jobID: String) async throws {}
}
