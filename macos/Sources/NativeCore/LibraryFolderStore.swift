import Foundation
import Darwin

public struct LibraryFolder: Codable, Equatable, Sendable {
    public var id: String
    public var name: String
    public var parentID: String?
    public init(id: String, name: String, parentID: String? = nil) {
        self.id = id; self.name = name; self.parentID = parentID
    }
}

public struct LibraryOrganization: Codable, Equatable, Sendable {
    public var schema_version: Int
    public var revision: Int
    public var folders: [LibraryFolder]
    public var assignments: [String: String]
    public init(schema_version: Int = 2, revision: Int = 0, folders: [LibraryFolder] = [], assignments: [String: String] = [:]) {
        self.schema_version = schema_version; self.revision = revision
        self.folders = folders; self.assignments = assignments
    }
}

public enum LibraryFolderStoreError: Error, LocalizedError, Sendable {
    case invalidName
    case duplicateName
    case unknownFolder(String)
    case unsupportedSchema(Int)
    case invalidOrganization
    case revisionExhausted
    case folderHasChildren
    case invalidParent
    case invalidBackup

    public var errorDescription: String? {
        switch self {
        case .invalidName: return "폴더 이름은 제어 문자 없이 1–100자로 입력하세요."
        case .duplicateName: return "같은 이름의 폴더가 이미 있습니다."
        case .unknownFolder: return "선택한 폴더가 존재하지 않습니다."
        case .unsupportedSchema: return "지원하지 않는 라이브러리 파일 버전입니다. 기존 파일을 보존합니다."
        case .invalidOrganization: return "라이브러리 파일이 유효하지 않습니다. 기존 파일을 보존합니다."
        case .folderHasChildren: return "하위 폴더를 먼저 이동하거나 삭제한 후 이 폴더를 삭제하세요."
        case .invalidParent: return "자기 자신이나 하위 폴더를 상위 폴더로 선택할 수 없습니다."
        case .invalidBackup: return "기존 라이브러리 백업 경로가 일반 파일이 아닙니다. 기존 파일을 보존합니다."
        case .revisionExhausted: return "라이브러리 변경 번호를 더 이상 증가시킬 수 없습니다."
        }
    }
}

/// Owns logical folder metadata only. Share one actor per state root to serialize writers.
/// All operations reread disk; no cached state can silently replace invalid persisted data.
public actor LibraryFolderStore {
    private let file: URL

    /// `root` is the application's stateRoot, not a recording or output directory.
    public init(root: URL) {
        file = root.appendingPathComponent("native", isDirectory: true).appendingPathComponent("library.json")
    }

    public func load() throws -> LibraryOrganization {
        try read().organization
    }

    private func read() throws -> (organization: LibraryOrganization, bytes: Data?) {
        let data: Data
        do { data = try Data(contentsOf: file) }
        catch {
            let failure = error as NSError
            if failure.domain == NSCocoaErrorDomain && failure.code == NSFileReadNoSuchFileError {
                return (LibraryOrganization(), nil)
            }
            throw error
        }
        let organization: LibraryOrganization
        do { organization = try JSONDecoder().decode(LibraryOrganization.self, from: data) }
        catch { throw LibraryFolderStoreError.invalidOrganization }
        try Self.validate(organization)
        return (organization, data)
    }

    public func create(name: String, parentID: String? = nil) throws -> LibraryOrganization {
        try mutate { organization in
            let name = try Self.validName(name)
            try Self.requireParent(parentID, in: organization.folders)
            try Self.requireUnique(name, in: organization.folders.filter { $0.parentID == parentID })
            var id = UUID().uuidString
            while organization.folders.contains(where: { $0.id == id }) { id = UUID().uuidString }
            organization.folders.append(LibraryFolder(id: id, name: name, parentID: parentID))
        }
    }

    public func rename(id: String, name: String) throws -> LibraryOrganization {
        try mutate { organization in
            guard let index = organization.folders.firstIndex(where: { $0.id == id }) else {
                throw LibraryFolderStoreError.unknownFolder(id)
            }
            let name = try Self.validName(name)
            try Self.requireUnique(name, in: organization.folders.filter { $0.id != id && $0.parentID == organization.folders[index].parentID })
            organization.folders[index].name = name
        }
    }

    /// Renames and moves a folder in a single metadata transaction.
    public func update(id: String, name: String, parentID: String?) throws -> LibraryOrganization {
        try mutate { organization in
            guard let index = organization.folders.firstIndex(where: { $0.id == id }) else {
                throw LibraryFolderStoreError.unknownFolder(id)
            }
            let name = try Self.validName(name)
            try Self.requireParent(parentID, in: organization.folders)
            if let parentID, parentID == id || LibraryFolderHierarchy.descendantIDs(of: id, in: organization.folders).contains(parentID) {
                throw LibraryFolderStoreError.invalidParent
            }
            try Self.requireUnique(name, in: organization.folders.filter { $0.id != id && $0.parentID == parentID })
            organization.folders[index].name = name
            organization.folders[index].parentID = parentID
        }
    }

    /// Removes the folder and its assignments; never opens, moves or deletes recordings.
    public func delete(id: String) throws -> LibraryOrganization {
        try mutate { organization in
            guard organization.folders.contains(where: { $0.id == id }) else {
                throw LibraryFolderStoreError.unknownFolder(id)
            }
            guard !organization.folders.contains(where: { $0.parentID == id }) else {
                throw LibraryFolderStoreError.folderHasChildren
            }
            organization.folders.removeAll { $0.id == id }
            organization.assignments = organization.assignments.filter { $0.value != id }
        }
    }

    /// Keys are opaque job.id strings; they need not equal recording UUIDs.
    public func assign(jobIDs: [String], folderID: String?) throws -> LibraryOrganization {
        try mutate { organization in
            if let folderID, !organization.folders.contains(where: { $0.id == folderID }) {
                throw LibraryFolderStoreError.unknownFolder(folderID)
            }
            for jobID in jobIDs { organization.assignments[jobID] = folderID }
        }
    }

    // No await between reading and writing: actor isolation covers the whole transaction.
    private func mutate(_ update: (inout LibraryOrganization) throws -> Void) throws -> LibraryOrganization {
        let original = try read()
        var organization = original.organization
        guard organization.revision < Int.max else { throw LibraryFolderStoreError.revisionExhausted }
        try update(&organization)
        organization.schema_version = 2
        organization.revision += 1
        try Self.validate(organization)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(organization)
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        if original.organization.schema_version == 1, let bytes = original.bytes {
            try preserveLegacyBackup(bytes)
        }
        try data.write(to: file, options: .atomic)
        return organization
    }

    private static func validName(_ raw: String) throws -> String {
        // Check raw scalars before trimming so newlines/tabs cannot disappear into a valid name.
        guard !raw.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw LibraryFolderStoreError.invalidName
        }
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines).precomposedStringWithCanonicalMapping
        guard !name.isEmpty, name.count <= 100 else { throw LibraryFolderStoreError.invalidName }
        return name
    }

    private static func nameKey(_ name: String) -> String {
        name.folding(options: .caseInsensitive, locale: Locale(identifier: "en_US_POSIX"))
    }

    private static func requireUnique(_ name: String, in folders: [LibraryFolder]) throws {
        let key = nameKey(name)
        if try folders.contains(where: { try nameKey(validName($0.name)) == key }) {
            throw LibraryFolderStoreError.duplicateName
        }
    }

    private static func requireParent(_ parentID: String?, in folders: [LibraryFolder]) throws {
        if let parentID, !folders.contains(where: { $0.id == parentID }) {
            throw LibraryFolderStoreError.unknownFolder(parentID)
        }
    }

    // Publish a complete backup with an atomic, exclusive hard link. Never replace history.
    private func preserveLegacyBackup(_ bytes: Data) throws {
        let backup = file.deletingLastPathComponent().appendingPathComponent("library-v1-backup.json")
        if try Self.regularBackupExists(backup) { return }
        let temporary = backup.deletingLastPathComponent().appendingPathComponent(".library-v1-backup-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: temporary) }
        try bytes.write(to: temporary, options: .atomic)
        do { try FileManager.default.linkItem(at: temporary, to: backup) }
        catch {
            // An independently created regular backup wins; an obstruction must fail.
            if try Self.regularBackupExists(backup) { return }
            throw error
        }
    }

    private static func regularBackupExists(_ url: URL) throws -> Bool {
        var status = stat()
        if lstat(url.path, &status) == 0 {
            guard (status.st_mode & S_IFMT) == S_IFREG else { throw LibraryFolderStoreError.invalidBackup }
            return true
        }
        let code = errno
        if code == ENOENT { return false }
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(code))
    }

    private static func validate(_ organization: LibraryOrganization) throws {
        guard organization.schema_version == 1 || organization.schema_version == 2 else {
            throw LibraryFolderStoreError.unsupportedSchema(organization.schema_version)
        }
        guard organization.revision >= 0 else { throw LibraryFolderStoreError.invalidOrganization }
        var ids = Set<String>()
        var names: [String?: Set<String>] = [:]
        var parents: [String: String] = [:]
        for folder in organization.folders {
            guard !folder.id.isEmpty, ids.insert(folder.id).inserted else {
                throw LibraryFolderStoreError.invalidOrganization
            }
            let key = nameKey(try validName(folder.name))
            guard names[folder.parentID, default: []].insert(key).inserted else {
                throw LibraryFolderStoreError.invalidOrganization
            }
            if let parent = folder.parentID {
                guard organization.schema_version == 2 else { throw LibraryFolderStoreError.invalidOrganization }
                parents[folder.id] = parent
            }
        }
        guard parents.values.allSatisfy({ ids.contains($0) }),
              organization.assignments.values.allSatisfy({ ids.contains($0) }) else {
            throw LibraryFolderStoreError.invalidOrganization
        }
        var finished = Set<String>()
        for id in ids where !finished.contains(id) {
            var chain = Set<String>()
            var current: String? = id
            while let node = current, !finished.contains(node) {
                guard chain.insert(node).inserted else { throw LibraryFolderStoreError.invalidOrganization }
                current = parents[node]
            }
            finished.formUnion(chain)
        }
    }
}
