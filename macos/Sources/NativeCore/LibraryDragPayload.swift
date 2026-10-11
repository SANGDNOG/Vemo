import Foundation

public enum LibraryDragKind: String, Codable, Sendable {
    case recording, folder
}

public struct LibraryDragPayload: Codable, Equatable, Sendable {
    public let version: Int
    public let kind: LibraryDragKind
    public let id: String
    public let sessionID: String
    public let dragID: String

    public init(kind: LibraryDragKind, id: String, sessionID: String, version: Int = 1, dragID: String = UUID().uuidString) {
        self.version = version
        self.kind = kind
        self.id = id
        self.sessionID = sessionID
        self.dragID = dragID
    }
}

public enum LibraryDropTarget: Equatable, Sendable {
    case folder(String), unfiled, root
}

public enum LibraryDropAction: Equatable, Sendable {
    case recording(id: String, folderID: String?)
    case folder(id: String, parentID: String?)
}

public enum LibraryDropError: Error, LocalizedError, Sendable {
    case unsupportedVersion(Int)
    case invalidPayload
    case unknownRecording(String)
    case invalidTarget

    public var errorDescription: String? {
        switch self {
        case .unsupportedVersion: return "지원하지 않는 라이브러리 드래그 버전입니다."
        case .invalidPayload: return "현재 앱에서 시작한 유효한 라이브러리 항목만 이동할 수 있습니다."
        case .unknownRecording: return "이동할 녹음이 존재하지 않습니다."
        case .invalidTarget: return "이 항목을 선택한 위치로 이동할 수 없습니다."
        }
    }
}

/// Preview rules; the store revalidates destinations against fresh persisted metadata.
public enum LibraryDropRules {
    public static func action(
        for payload: LibraryDragPayload,
        target: LibraryDropTarget,
        organization: LibraryOrganization,
        knownJobIDs: Set<String>,
        sessionID: String
    ) throws -> LibraryDropAction? {
        guard payload.version == 1 else { throw LibraryDropError.unsupportedVersion(payload.version) }
        guard !payload.id.isEmpty, !payload.sessionID.isEmpty, payload.sessionID == sessionID else {
            throw LibraryDropError.invalidPayload
        }

        switch payload.kind {
        case .recording:
            guard knownJobIDs.contains(payload.id) else { throw LibraryDropError.unknownRecording(payload.id) }
            let folderID: String?
            switch target {
            case .folder(let id):
                guard organization.folders.contains(where: { $0.id == id }) else {
                    throw LibraryFolderStoreError.unknownFolder(id)
                }
                folderID = id
            case .unfiled: folderID = nil
            case .root: throw LibraryDropError.invalidTarget
            }
            guard organization.assignments[payload.id] != folderID else { return nil }
            return .recording(id: payload.id, folderID: folderID)

        case .folder:
            guard let folder = organization.folders.first(where: { $0.id == payload.id }) else {
                throw LibraryFolderStoreError.unknownFolder(payload.id)
            }
            let parentID: String?
            switch target {
            case .folder(let id):
                guard organization.folders.contains(where: { $0.id == id }) else {
                    throw LibraryFolderStoreError.unknownFolder(id)
                }
                guard id != folder.id,
                      !LibraryFolderHierarchy.descendantIDs(of: folder.id, in: organization.folders).contains(id) else {
                    throw LibraryFolderStoreError.invalidParent
                }
                parentID = id
            case .root: parentID = nil
            case .unfiled: throw LibraryDropError.invalidTarget
            }
            guard folder.parentID != parentID else { return nil }
            try LibraryFolderStore.requireUnique(
                LibraryFolderStore.validName(folder.name),
                in: organization.folders.filter { $0.id != folder.id && $0.parentID == parentID }
            )
            return .folder(id: folder.id, parentID: parentID)
        }
    }
}
