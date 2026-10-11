import Foundation

public struct LibraryFolder: Codable, Equatable, Sendable {
    public var id: String
    public var name: String
    public init(id: String, name: String) { self.id = id; self.name = name }
}

public struct LibraryOrganization: Codable, Equatable, Sendable {
    public var schema_version: Int
    public var revision: Int
    public var folders: [LibraryFolder]
    public var assignments: [String: String]
    public init(schema_version: Int = 1, revision: Int = 0, folders: [LibraryFolder] = [], assignments: [String: String] = [:]) {
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

    public var errorDescription: String? {
        switch self {
        case .invalidName: return "폴더 이름은 제어 문자 없이 1–100자로 입력하세요."
        case .duplicateName: return "같은 이름의 폴더가 이미 있습니다."
        case .unknownFolder: return "선택한 폴더가 존재하지 않습니다."
        case .unsupportedSchema: return "지원하지 않는 라이브러리 파일 버전입니다. 기존 파일을 보존합니다."
        case .invalidOrganization: return "라이브러리 파일이 유효하지 않습니다. 기존 파일을 보존합니다."
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
        let data: Data
        do { data = try Data(contentsOf: file) }
        catch {
            let failure = error as NSError
            if failure.domain == NSCocoaErrorDomain && failure.code == NSFileReadNoSuchFileError {
                return LibraryOrganization()
            }
            throw error
        }
        let organization: LibraryOrganization
        do { organization = try JSONDecoder().decode(LibraryOrganization.self, from: data) }
        catch { throw LibraryFolderStoreError.invalidOrganization }
        try Self.validate(organization)
        return organization
    }

    public func create(name: String) throws -> LibraryOrganization {
        try mutate { organization in
            let name = try Self.validName(name)
            try Self.requireUnique(name, in: organization.folders)
            var id = UUID().uuidString
            while organization.folders.contains(where: { $0.id == id }) { id = UUID().uuidString }
            organization.folders.append(LibraryFolder(id: id, name: name))
        }
    }

    public func rename(id: String, name: String) throws -> LibraryOrganization {
        try mutate { organization in
            guard let index = organization.folders.firstIndex(where: { $0.id == id }) else {
                throw LibraryFolderStoreError.unknownFolder(id)
            }
            let name = try Self.validName(name)
            try Self.requireUnique(name, in: organization.folders.filter { $0.id != id })
            organization.folders[index].name = name
        }
    }

    /// Removes the folder and its assignments; never opens, moves or deletes recordings.
    public func delete(id: String) throws -> LibraryOrganization {
        try mutate { organization in
            guard organization.folders.contains(where: { $0.id == id }) else {
                throw LibraryFolderStoreError.unknownFolder(id)
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
        var organization = try load()
        guard organization.revision < Int.max else { throw LibraryFolderStoreError.revisionExhausted }
        try update(&organization)
        organization.revision += 1
        try Self.validate(organization)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(organization)
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
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

    private static func validate(_ organization: LibraryOrganization) throws {
        guard organization.schema_version == 1 else {
            throw LibraryFolderStoreError.unsupportedSchema(organization.schema_version)
        }
        guard organization.revision >= 0 else { throw LibraryFolderStoreError.invalidOrganization }
        var ids = Set<String>(), names = Set<String>()
        for folder in organization.folders {
            guard !folder.id.isEmpty, ids.insert(folder.id).inserted else {
                throw LibraryFolderStoreError.invalidOrganization
            }
            let key = nameKey(try validName(folder.name))
            guard names.insert(key).inserted else { throw LibraryFolderStoreError.invalidOrganization }
        }
        guard organization.assignments.values.allSatisfy({ ids.contains($0) }) else {
            throw LibraryFolderStoreError.invalidOrganization
        }
    }
}
