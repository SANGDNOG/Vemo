import AppKit
import Combine
import CryptoKit
import Foundation
import NativeCore
import UniformTypeIdentifiers
import Darwin

@MainActor
final class GoogleConnectionModel: ObservableObject {
    @Published private(set) var isBusy = false
    @Published private(set) var status = "Google 연결 전 · Desktop OAuth JSON을 선택하세요."
    @Published private(set) var isConnected = false
    @Published private(set) var hasClientConfiguration = false
    @Published private(set) var hasBinding = false
    @Published private(set) var needsKeychainRecovery = false
    var canDisconnect: Bool { hasAuthorization || isConnected }
    var canSelectClient: Bool { !isBusy && !isConnected && !hasBinding && !hasAuthorization && !closing }

    private let broker: MobileSyncBroker
    private let store: GoogleClientConfigurationFile
    private let stateDirectory: URL
    private let root: URL
    private var oauth: GoogleOAuthClient?
    private var mirror: GoogleDriveMirror?
    private var operation: Task<Bool, Never>?
    private var panel: NSOpenPanel?
    private var cancellation: Task<Void, Never>?
    private var restored = false
    private var nextRestoreAttemptAt: Date?
    private var closing = false
    private var hasAuthorization = false
    private let displayName = "Vemo 테스트"

    init(root: URL) {
        self.root = root
        stateDirectory = root.appendingPathComponent("native/google-drive", isDirectory: true)
        store = GoogleClientConfigurationFile(directory: stateDirectory)
        broker = MobileSyncBroker(root: root, namespace: .googleDrive)
    }

    func selectClientJSON() {
        guard canSelectClient else { return }
        do {
            try loadLocalConfiguration()
            guard canSelectClient else { return }
        } catch { status = message(error); return }
        let chooser = NSOpenPanel()
        chooser.allowedContentTypes = [.json]
        chooser.canChooseDirectories = false
        chooser.allowsMultipleSelection = false
        chooser.message = "Google Cloud에서 받은 Desktop 앱용 OAuth JSON을 선택하세요."
        panel = chooser; isBusy = true
        chooser.begin { [weak self] response in
            Task { @MainActor in
                guard let self else { return }
                defer { self.panel = nil; self.isBusy = false }
                guard !self.closing, response == .OK, let url = chooser.url else { return }
                do {
                    // Recheck durable binding after the asynchronous picker completes.
                    try self.loadLocalConfiguration()
                    guard !self.hasBinding, !self.hasAuthorization, !self.isConnected else { throw GoogleConnectionFailure.binding }
                    let scoped = url.startAccessingSecurityScopedResource()
                    defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                    let bytes = try GoogleClientConfigurationFile.readSelected(url)
                    let configuration = try GoogleOAuthClientConfiguration.loadInstalledJSON(bytes)
                    try self.store.save(bytes)
                    self.install(configuration)
                    self.status = "OAuth 설정을 이 Mac에 저장했습니다. Google 연결을 눌러 승인하세요."
                } catch { self.status = self.message(error) }
            }
        }
    }

    func connect() {
        guard !isBusy, !closing, !needsKeychainRecovery else { return }
        stopAutomaticRestore()
        start { [self] in
            do {
                try loadLocalConfiguration()
                guard let oauth, let mirror else { throw GoogleConnectionFailure.configuration }
                isConnected = false
                status = "브라우저에서 Google 계정과 drive.file 권한을 승인하세요."
                try await oauth.connect { url in try await Self.openAuthorization(url) }
                hasAuthorization = true
                try Task.checkCancellation()
                let processor = try await broker.processorIdentity()
                _ = try await mirror.createRoot(displayName: displayName, processorID: processor)
                hasBinding = true
                _ = try await mirror.prepare(broker: broker, displayName: displayName)
                try Task.checkCancellation()
                isConnected = true
                status = "Google 연결됨 · Vemo 테스트 전용 폴더의 원격 준비를 확인했습니다."
            } catch {
                hasBinding = hasBinding || ((try? store.hasJournal()) ?? true)
                status = message(error)
            }
            return false
        }
    }

    /// User-button-only path; startup restore and sync never call this method.
    func recoverKeychainAccess() {
        guard needsKeychainRecovery, !isBusy, !closing else { return }
        stopAutomaticRestore()
        start { [self] in
            do {
                status = "macOS에서 기존 Keychain 항목 접근을 승인하세요. Google 재동의와는 별개입니다."
                // Deliberately bypass loadLocalConfiguration's noninteractive token probe.
                hasBinding = try store.hasJournal()
                guard let bytes = try store.load() else { throw GoogleConnectionFailure.configuration }
                let configuration = try GoogleOAuthClientConfiguration.loadInstalledJSON(bytes)
                let vault = GoogleOAuthKeychainVault(account: Self.keychainAccount(root: root, clientID: configuration.clientID))
                let found = try await vault.recoverRefreshTokenAccess()
                try Task.checkCancellation()
                guard !closing else { throw CancellationError() }
                guard found else { throw GoogleOAuthError.notConnected }
                // All subsequent token access, including automatic refresh, stays noninteractive.
                guard try vault.loadRefreshToken() != nil else { throw GoogleOAuthError.notConnected }
                try Task.checkCancellation()
                guard !closing else { throw CancellationError() }
                hasAuthorization = true
                needsKeychainRecovery = false
                install(configuration)
                guard let oauth, let mirror else { throw GoogleConnectionFailure.configuration }
                guard let binding = try await mirror.restore() else {
                    isConnected = false
                    status = "Keychain 접근 복구됨 · Google 연결을 눌러 전용 폴더 연결을 준비하세요."
                    return false
                }
                guard binding.processorID == (try await broker.processorIdentity()) else { throw GoogleConnectionFailure.binding }
                _ = try await oauth.accessToken()
                hasAuthorization = true
                _ = try await mirror.prepare(broker: broker, displayName: displayName)
                try Task.checkCancellation()
                guard !closing else { throw CancellationError() }
                needsKeychainRecovery = false; isConnected = true
                status = "Keychain 접근 복구됨 · 기존 Google 계정과 전용 폴더를 확인했습니다."
            } catch {
                if (error as? GoogleOAuthError) == .notConnected { needsKeychainRecovery = false }
                status = message(error)
            }
            return false
        }
    }

    func disconnect() {
        guard !isBusy, !closing else { return }
        stopAutomaticRestore()
        start { [self] in
            do {
                try loadLocalConfiguration()
                isConnected = false
                if let oauth { try await oauth.disconnect() }
                isConnected = false; hasAuthorization = false
                status = "이 Mac의 Google 토큰을 삭제했습니다. 원격 파일·계정 권한·기존 폴더 연결 기록은 보존됩니다."
            } catch { status = message(error) }
            return false
        }
    }

    func cancel() {
        guard isBusy else { return }
        stopAutomaticRestore()
        if let panel { panel.cancel(nil); return }
        operation?.cancel()
        let current = oauth
        // Keep isBusy until cancellation has drained, preventing a late cancel from hitting a new operation.
        cancellation = Task { await current?.cancel() }
        status = "취소 중 · 완료된 로컬 저장과 원격 작업은 보존됩니다."
    }

    func restoreIfNeeded(now: Date = Date()) async {
        guard !restored, !isBusy, !closing else { return }
        if let nextRestoreAttemptAt, now < nextRestoreAttemptAt { return }
        restored = true
        nextRestoreAttemptAt = nil
        start { [self] in
            do {
                try loadLocalConfiguration()
                guard let oauth, let mirror else { return false }
                guard let binding = try await mirror.restore() else {
                    status = "OAuth 설정이 준비되었습니다. Google 연결을 눌러 전용 폴더를 준비하세요."
                    return false
                }
                hasBinding = true
                guard binding.processorID == (try await broker.processorIdentity()) else { throw GoogleConnectionFailure.binding }
                _ = try await oauth.accessToken()
                hasAuthorization = true
                _ = try await mirror.prepare(broker: broker, displayName: displayName)
                try Task.checkCancellation()
                isConnected = true
                status = "Google 연결 복원됨 · 기존 계정과 전용 폴더를 확인했습니다."
            } catch {
                status = message(error)
                if !Task.isCancelled, !closing, Self.isTransientRestoreFailure(error) {
                    restored = false
                    nextRestoreAttemptAt = now.addingTimeInterval(30)
                    status += " · 다음 자동 확인에서 연결을 다시 시도합니다."
                }
            }
            return false
        }
        _ = await operation?.value
    }

    private func stopAutomaticRestore() {
        restored = true
        nextRestoreAttemptAt = nil
    }

    private static func isTransientRestoreFailure(_ error: any Error) -> Bool {
        if (error as? GoogleOAuthError) == .transport { return true }
        if case .http(let code) = error as? GoogleDriveError {
            return code == 429 || (500...599).contains(code)
        }
        if let urlError = error as? URLError {
            switch urlError.code {
            case .timedOut, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed,
                 .networkConnectionLost, .notConnectedToInternet: return true
            default: return false
            }
        }
        return false
    }

    func synchronize(imports: ImportBroker, jobs: @escaping @MainActor @Sendable () -> [Job]) async -> Bool {
        guard !isBusy, !closing, isConnected, let mirror else { return false }
        start { [self] in
            var added = false
            do {
                status = "Google 원격 녹음을 확인하고 있습니다."
                var pullIncomplete = false
                var itemDiagnostic: String?
                do { _ = try await mirror.pull() }
                catch {
                    if Task.isCancelled || error is CancellationError || Self.isConnectionFailure(error)
                        || (error as? GoogleDriveError) == .conflict { throw error }
                    // Scan only already committed/validated local markers even if another remote item failed.
                    pullIncomplete = true
                    itemDiagnostic = Self.safeDiagnosticCode(error)
                }
                try Task.checkCancellation()
                let report = try await broker.scan(importBroker: imports)
                added = !report.pending.isEmpty
                let mappings = try await broker.mappings()
                var uploaded = 0, failures = report.issues.count + (pullIncomplete ? 1 : 0)
                for mapping in mappings where !mapping.suppressed {
                    try Task.checkCancellation()
                    let matches = jobs().filter { $0.uuid == mapping.memoUUID && $0.deleted_at == nil && $0.purge_pending != true && $0.wait_reason != "source_refresh" }
                    guard matches.count <= 1 else { failures += 1; continue }
                    guard let job = matches.first else { continue }
                    do {
                        guard mapping.jobID == nil || mapping.jobID == job.id else { throw GoogleConnectionFailure.binding }
                        if !mapping.delivered { try await broker.bindJob(memoUUID: job.uuid, jobID: job.id) }
                        // Binding may suspend; obtain the current eligible revision before asking Core to publish.
                        let destination = stateDirectory.path
                        let shouldPublish: @Sendable () async -> Bool = { [weak self] in
                            await MainActor.run {
                                guard let self, !self.closing, self.isConnected, !Task.isCancelled else { return false }
                                let current = jobs().filter { $0.uuid == job.uuid }
                                guard current.count == 1, let latest = current.first,
                                      latest.id == job.id, latest.uuid == job.uuid,
                                      latest.deleted_at == nil, latest.purge_pending != true,
                                      latest.wait_reason != "source_refresh",
                                      latest.status == "completed", latest.notes_status == "completed",
                                      latest.recorded_at == job.recorded_at else { return false }
                                return AutomaticRecordingPolicy.zipKey(latest, destination: destination)
                                    == AutomaticRecordingPolicy.zipKey(job, destination: destination)
                            }
                        }
                        guard job.status == "completed", job.notes_status == "completed",
                              await shouldPublish() else { continue }
                        try await mirror.pushResult(job: job, broker: broker, shouldPublish: shouldPublish)
                        uploaded += 1
                    } catch {
                        if Task.isCancelled || error is CancellationError || (error as? GoogleOAuthError) == .cancelled { throw CancellationError() }
                        if Self.isConnectionFailure(error) { throw error }
                        failures += 1
                        if itemDiagnostic == nil { itemDiagnostic = Self.safeDiagnosticCode(error) }
                    }
                }
                try Task.checkCancellation()
                status = "로컬 가져오기 대기 \(report.pending.count)개 · Google 원격 결과 확인 \(uploaded)개"
                    + (failures > 0 ? " · 확인 필요 \(failures)건은 보존하고 다음 확인에서 재시도합니다." : " · 30초마다 확인합니다.")
                    + (itemDiagnostic.map { " [" + $0 + "]" } ?? "")
            } catch { status = message(error) }
            return added
        }
        guard let current = operation else { return false }
        return await withTaskCancellationHandler { await current.value } onCancel: { current.cancel() }
    }

    func shutdown() async {
        closing = true
        panel?.cancel(nil)
        operation?.cancel()
        await oauth?.cancel()
        _ = await operation?.value
    }

    private func start(_ action: @escaping @MainActor () async -> Bool) {
        isBusy = true
        operation = Task { [self] in
            defer { isBusy = false; operation = nil }
            let result = await action()
            await cancellation?.value
            cancellation = nil
            return result
        }
    }
    /// Pure identity derivation: stable across updates and isolated by state root and OAuth client.
    static func keychainAccount(root: URL, clientID: String) -> String {
        let rootHash = SHA256.hash(data: Data(root.path.utf8)).map { String(format: "%02x", $0) }.joined()
        return "root-sha256:\(rootHash):client:\(clientID)"
    }

    private func install(_ configuration: GoogleOAuthClientConfiguration) {
        let client = GoogleOAuthClient(configuration: configuration, vault: GoogleOAuthKeychainVault(account: Self.keychainAccount(root: root, clientID: configuration.clientID)))
        oauth = client
        mirror = GoogleDriveMirror(stateDirectory: stateDirectory, client: GoogleDriveClient(accessToken: { try await client.accessToken() }))
        hasClientConfiguration = true
    }
    private func loadLocalConfiguration() throws {
        hasBinding = try store.hasJournal()
        guard !hasClientConfiguration else { return }
        guard let bytes = try store.load() else {
            if hasBinding { throw GoogleConnectionFailure.binding }
            return
        }
        let configuration = try GoogleOAuthClientConfiguration.loadInstalledJSON(bytes)
        install(configuration)
        hasAuthorization = try GoogleOAuthKeychainVault(account: Self.keychainAccount(root: root, clientID: configuration.clientID)).loadRefreshToken() != nil
    }
    private static func isConnectionFailure(_ error: any Error) -> Bool {
        if let oauth = error as? GoogleOAuthError { return oauth == .reauthorizationRequired || oauth == .notConnected }
        if let drive = error as? GoogleDriveError {
            return drive == .accountMismatch || drive == .notConnected || drive == .http(401) || drive == .http(403)
        }
        return false
    }
    // Only allowlisted categories enter observable diagnostics; never stringify arbitrary errors.
    static func safeDiagnosticCode(_ error: any Error) -> String {
        if error is CancellationError { return "cancelled" }
        if let oauth = error as? GoogleOAuthError { return "oauth." + oauth.rawValue }
        if let drive = error as? GoogleDriveError {
            switch drive {
            case .invalidResponse: return "drive.invalidResponse"
            case .http(let code): return (100...599).contains(code) ? "drive.http.\(code)" : "drive.http.invalidStatus"
            case .limit: return "drive.limit"
            case .conflict: return "drive.conflict"
            case .invalidMetadata: return "drive.invalidMetadata"
            case .accountMismatch: return "drive.accountMismatch"
            case .notConnected: return "drive.notConnected"
            case .busy: return "drive.busy"
            case .staleJob: return "drive.staleJob"
            }
        }
        if let broker = error as? BrokerFailure {
            let allowed: Set<String> = ["overlapping_folders", "unavailable_file", "broker_busy",
                "empty_notes", "empty_transcript", "invalid_sync_configuration", "mobile_identity_conflict",
                "mobile_job_conflict", "mobile_not_configured", "mobile_path_conflict", "mobile_registry",
                "mobile_result_conflict", "mobile_result_not_ready", "mobile_source_mismatch", "oversize_file",
                "processor_conflict", "source_changing", "sync_item_limit", "sync_root_changed",
                "unknown_mobile_mapping", "audio_hash_mismatch", "audio_size_mismatch", "source_incomplete",
                "sync_root_unavailable", "unsafe_sync_directory", "unsafe_sync_file", "waiting_for_download",
                "invalid_date", "invalid_hash", "invalid_recording", "invalid_uuid", "mobile_schema"]
            return allowed.contains(broker.code) ? "broker." + broker.code : "broker.unclassified"
        }
        if let local = error as? GoogleConnectionFailure {
            switch local {
            case .configuration: return "local.configuration"
            case .storage: return "local.storage"
            case .binding: return "local.binding"
            case .browser: return "local.browser"
            }
        }
        return "unclassified"
    }
    private func message(_ error: any Error) -> String {
        if (error as? GoogleOAuthError) == .storage { needsKeychainRecovery = true; isConnected = false }
        return messageText(error) + " [" + Self.safeDiagnosticCode(error) + "]"
    }
    private func messageText(_ error: any Error) -> String {
        if Task.isCancelled || error is CancellationError || (error as? GoogleOAuthError) == .cancelled {
            return "작업을 취소했습니다. 이미 저장된 로컬 파일과 원격 파일은 보존됩니다."
        }
        if Self.isConnectionFailure(error) {
            isConnected = false
            return "Google 승인을 다시 확인해야 합니다. 기존 계정과 전용 폴더 기록은 보존했습니다. 같은 계정으로 다시 연결하세요."
        }
        if let oauth = error as? GoogleOAuthError {
            switch oauth {
            case .accessDenied: return "Google 승인이 거부되었습니다. 연결을 눌러 다시 승인할 수 있습니다."
            case .expired: return "Google 승인 대기 시간이 지났습니다. 다시 연결하세요."
            case .storage: return "Keychain 접근을 확인하지 못했습니다. ‘Keychain 접근 복구’에서 macOS 접근을 승인하세요. Google 재동의는 시작하지 않습니다."
            case .invalidConfiguration: return "Desktop 앱용 OAuth JSON 형식을 확인하세요."
            default: return "Google 연결을 완료하지 못했습니다. 기존 파일을 보존했습니다. 다시 시도하세요."
            }
        }
        if (error as? GoogleConnectionFailure) == .configuration { return "먼저 Desktop 앱용 OAuth JSON을 선택하세요." }
        if (error as? GoogleConnectionFailure) == .browser { return "설치된 브라우저를 열지 못했습니다. 브라우저 상태를 확인하고 다시 연결하세요." }
        if (error as? GoogleConnectionFailure) == .binding || (error as? GoogleDriveError) == .conflict {
            isConnected = false
            return "기존 계정·전용 폴더·처리기 연결과 충돌합니다. 자동 전환하지 않고 기존 기록을 보존했습니다."
        }
        if let drive = error as? GoogleDriveError {
            switch drive {
            case .http(404): return "Google 전용 폴더 또는 항목을 찾지 못했습니다. 기존 연결 기록을 보존했습니다."
            case .http(429): return "Google 요청 한도에 도달했습니다. 다음 확인에서 다시 시도합니다."
            case .http: return "Google 서버 요청이 실패했습니다. HTTP 상태 코드를 확인하고 다시 시도하세요."
            case .invalidResponse: return "Google 응답 형식을 확인하지 못했습니다. 기존 데이터를 보존했습니다."
            case .invalidMetadata: return "Google 파일의 식별자·부모·Vemo 메타데이터를 확인하지 못했습니다. 기존 파일을 보존했습니다."
            case .limit: return "Google 응답의 크기 또는 항목 수가 허용 범위를 초과했습니다."
            case .busy: return "Google 폴더 작업이 이미 진행 중입니다. 다음 확인에서 다시 시도합니다."
            case .staleJob: return "작업이 변경되어 이전 결과의 업로드를 중단했습니다. 다음 확인에서 현재 작업을 다시 확인합니다."
            default: break
            }
        }
        if let broker = error as? BrokerFailure {
            switch broker.code {
            case "overlapping_folders": return "Google 캐시를 로컬 가져오기 경로에 등록하지 못했습니다. 기존 연결과 파일을 보존했습니다."
            case "broker_busy": return "로컬 가져오기 작업이 진행 중입니다. 다음 확인에서 다시 시도합니다."
            case "processor_conflict", "mobile_identity_conflict", "mobile_job_conflict", "mobile_result_conflict":
                return "로컬 가져오기 식별자와 기존 기록이 충돌합니다. 기존 파일을 보존했습니다."
            default: return "로컬 가져오기 또는 결과 저장을 완료하지 못했습니다. 진단 코드를 확인하세요. 기존 데이터를 보존했습니다."
            }
        }
        if (error as? GoogleConnectionFailure) == .storage { return "이 Mac의 OAuth 설정 파일을 안전하게 읽거나 저장하지 못했습니다. 파일 권한과 경로를 확인하세요." }
        return "Google 작업을 완료하지 못했습니다. 기존 데이터는 보존되며 다음 확인에서 다시 시도합니다."
    }
    private static func openAuthorization(_ url: URL) async throws {
        try Task.checkCancellation()
        let workspace = NSWorkspace.shared
        if let chrome = workspace.urlForApplication(withBundleIdentifier: "com.google.Chrome") {
            let configuration = NSWorkspace.OpenConfiguration()
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                workspace.open([url], withApplicationAt: chrome, configuration: configuration) { application, _ in
                    if application != nil { continuation.resume() }
                    else { continuation.resume(throwing: GoogleConnectionFailure.browser) }
                }
            }
        } else if !workspace.open(url) { throw GoogleConnectionFailure.browser }
    }
}

private enum GoogleConnectionFailure: Error { case configuration, storage, binding, browser }

/// Descriptor-relative access prevents symlink traversal for both imported and private configuration files.
private struct GoogleClientConfigurationFile {
    let directory: URL
    private let filename = "installed-client.json"
    private static let limit = 64 * 1024

    private static func openDirectory(_ url: URL, create: Bool) throws -> Int32? {
        guard url.isFileURL, url.path.hasPrefix("/") else { throw GoogleConnectionFailure.storage }
        var fd = Darwin.open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        guard fd >= 0 else { throw GoogleConnectionFailure.storage }
        do {
            for component in url.pathComponents.dropFirst() {
                guard component != ".", component != "..", !component.contains("/") else { throw GoogleConnectionFailure.storage }
                // Client configuration must never be persisted in a source checkout.
                var git = stat()
                if fstatat(fd, ".git", &git, AT_SYMLINK_NOFOLLOW) == 0 { throw GoogleConnectionFailure.storage }
                var next = openat(fd, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                if next < 0, errno == ENOENT, create {
                    guard mkdirat(fd, component, 0o700) == 0 || errno == EEXIST else { throw GoogleConnectionFailure.storage }
                    next = openat(fd, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                }
                if next < 0, errno == ENOENT, !create { close(fd); return nil }
                guard next >= 0 else { throw GoogleConnectionFailure.storage }
                close(fd); fd = next
            }
            var git = stat()
            guard fstatat(fd, ".git", &git, AT_SYMLINK_NOFOLLOW) != 0 else { throw GoogleConnectionFailure.storage }
            return fd
        } catch { close(fd); throw error }
    }
    private static func read(_ name: String, directory: Int32, privateFile: Bool) throws -> Data? {
        let fd = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
        if fd < 0, errno == ENOENT { return nil }
        guard fd >= 0 else { throw GoogleConnectionFailure.storage }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_uid == getuid(),
              info.st_nlink == 1, info.st_size > 0, info.st_size <= limit,
              !privateFile || info.st_mode & 0o777 == 0o600 else { throw GoogleConnectionFailure.storage }
        var data = Data(), buffer = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = Darwin.read(fd, &buffer, buffer.count)
            if count < 0, errno == EINTR { continue }
            guard count >= 0, data.count + count <= limit else { throw GoogleConnectionFailure.storage }
            if count == 0 { break }
            data.append(contentsOf: buffer.prefix(count))
        }
        guard data.count == info.st_size else { throw GoogleConnectionFailure.storage }
        return data
    }
    static func readSelected(_ url: URL) throws -> Data {
        guard let fd = try openDirectory(url.deletingLastPathComponent(), create: false) else { throw GoogleConnectionFailure.storage }
        defer { close(fd) }
        guard let bytes = try read(url.lastPathComponent, directory: fd, privateFile: false) else { throw GoogleConnectionFailure.storage }
        return bytes
    }
    func load() throws -> Data? {
        guard let fd = try Self.openDirectory(directory, create: false) else { return nil }
        defer { close(fd) }
        return try Self.read(filename, directory: fd, privateFile: true)
    }
    func hasJournal() throws -> Bool {
        guard let fd = try Self.openDirectory(directory, create: false) else { return false }
        defer { close(fd) }
        var info = stat()
        if fstatat(fd, "drive.json", &info, AT_SYMLINK_NOFOLLOW) == 0 { return true }
        guard errno == ENOENT else { throw GoogleConnectionFailure.storage }
        return false
    }
    func save(_ bytes: Data) throws {
        guard !bytes.isEmpty, bytes.count <= Self.limit, let fd = try Self.openDirectory(directory, create: true) else { throw GoogleConnectionFailure.storage }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_uid == getuid(), fchmod(fd, 0o700) == 0 else { throw GoogleConnectionFailure.storage }
        let lock = openat(fd, "drive.lock", O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK, 0o600)
        guard lock >= 0 else { throw GoogleConnectionFailure.storage }
        defer { close(lock) }
        guard fstat(lock, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_uid == getuid(),
              info.st_nlink == 1, flock(lock, LOCK_EX | LOCK_NB) == 0 else { throw GoogleConnectionFailure.storage }
        defer { flock(lock, LOCK_UN) }
        // Refuse replacement once any root journal/intent exists, including a failed remote creation.
        guard fstatat(fd, "drive.json", &info, AT_SYMLINK_NOFOLLOW) != 0, errno == ENOENT else { throw GoogleConnectionFailure.binding }
        _ = try Self.read(filename, directory: fd, privateFile: true)
        let temporary = ".client-" + UUID().uuidString
        let file = openat(fd, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard file >= 0 else { throw GoogleConnectionFailure.storage }
        defer { close(file); unlinkat(fd, temporary, 0) }
        try bytes.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let written = Darwin.write(file, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                if written < 0, errno == EINTR { continue }
                guard written > 0 else { throw GoogleConnectionFailure.storage }
                offset += written
            }
        }
        guard fchmod(file, 0o600) == 0, fsync(file) == 0,
              renameat(fd, temporary, fd, filename) == 0, fsync(fd) == 0 else { throw GoogleConnectionFailure.storage }
    }
}
