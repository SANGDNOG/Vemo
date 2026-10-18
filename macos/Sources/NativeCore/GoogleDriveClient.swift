import Foundation

public enum GoogleDriveError: Error, Sendable, Equatable {
    case invalidResponse, http(Int), limit, conflict, invalidMetadata, accountMismatch, notConnected, busy, staleJob
}

/// Implementations must enforce the byte bound while receiving, reject redirects,
/// and never log authorization headers. The default implementation streams bodies.
public protocol GoogleDriveTransport: Sendable {
    func send(_ request: URLRequest, maximumBytes: Int64,
              receive: @escaping @Sendable (Data) throws -> Void) async throws -> Int
}

public struct GoogleDriveURLTransport: GoogleDriveTransport {
    public init() {}
    public func send(_ request: URLRequest, maximumBytes: Int64,
                     receive: @escaping @Sendable (Data) throws -> Void) async throws -> Int {
        let delegate = GoogleDriveRedirectGuard()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 3600
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        let (bytes, response) = try await session.bytes(for: request)
        guard let response = response as? HTTPURLResponse, response.url == request.url else { throw GoogleDriveError.invalidResponse }
        guard response.expectedContentLength <= maximumBytes else { throw GoogleDriveError.limit }
        var count: Int64 = 0, block = Data()
        for try await byte in bytes {
            try Task.checkCancellation()
            count += 1
            guard count <= maximumBytes else { throw GoogleDriveError.limit }
            block.append(byte)
            if block.count == 65_536 { try receive(block); block.removeAll(keepingCapacity: true) }
        }
        if !block.isEmpty { try receive(block) }
        return response.statusCode
    }
}
private final class GoogleDriveRedirectGuard: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
private final class GoogleDriveBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Data()
    let limit: Int64
    init(limit: Int64) { self.limit = limit }
    func append(_ data: Data) throws {
        lock.lock(); defer { lock.unlock() }
        guard Int64(value.count) + Int64(data.count) <= limit else { throw GoogleDriveError.limit }
        value.append(data)
    }
    func data() -> Data { lock.lock(); defer { lock.unlock() }; return value }
}

public struct GoogleDriveFile: Codable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let mimeType: String
    public let parents: [String]?
    public let properties: [String: String]?
    public let size: String?
    public let trashed: Bool?
}
public struct GoogleDriveBinding: Codable, Sendable, Equatable {
    public let accountID: String
    public let rootID: String
    public let processorID: String
    public init(accountID: String, rootID: String, processorID: String) {
        self.accountID = accountID; self.rootID = rootID; self.processorID = processorID
    }
}

public actor GoogleDriveClient {
    public static let folderMIME = "application/vnd.google-apps.folder"
    public static let jsonMIME = "application/json"
    public static let audioMIME = "audio/mp4"
    public static let jsonLimit: Int64 = 8 * 1024 * 1024
    private let accessToken: @Sendable () async throws -> String
    private let transport: any GoogleDriveTransport
    private let fields = "id,name,mimeType,parents,properties,size,trashed"
    public init(accessToken: @escaping @Sendable () async throws -> String,
                transport: any GoogleDriveTransport = GoogleDriveURLTransport()) {
        self.accessToken = accessToken; self.transport = transport
    }
    static func identifier(_ value: String) throws {
        guard !value.isEmpty, value.utf8.count <= 256,
              value.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95 }) else { throw GoogleDriveError.invalidMetadata }
    }
    private func request(_ path: String, query: [URLQueryItem] = [], method: String = "GET", body: Data? = nil,
                         contentType: String = "application/json") async throws -> URLRequest {
        try Task.checkCancellation()
        var url = URLComponents(string: "https://www.googleapis.com" + path)!
        url.queryItems = query.isEmpty ? nil : query
        guard let endpoint = url.url, endpoint.scheme == "https", endpoint.host == "www.googleapis.com", endpoint.port == nil,
              endpoint.user == nil, endpoint.password == nil, endpoint.fragment == nil else { throw GoogleDriveError.invalidMetadata }
        let token = try await accessToken()
        guard !token.isEmpty, token.utf8.count <= 16_384, token.utf8.allSatisfy({ (33...126).contains($0) }) else { throw GoogleDriveError.invalidMetadata }
        var request = URLRequest(url: endpoint)
        request.httpMethod = method; request.httpBody = body
        request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        return request
    }
    private func data(_ path: String, query: [URLQueryItem] = [], method: String = "GET", body: Data? = nil,
                      contentType: String = "application/json", shouldPublish: (@Sendable () async -> Bool)? = nil) async throws -> Data {
        let request = try await request(path, query: query, method: method, body: body, contentType: contentType)
        let buffer = GoogleDriveBuffer(limit: Self.jsonLimit)
        if let shouldPublish { try await Self.checkPublication(shouldPublish) }
        let status = try await transport.send(request, maximumBytes: Self.jsonLimit) { try buffer.append($0) }
        try Task.checkCancellation()
        guard (200...299).contains(status) else { throw GoogleDriveError.http(status) }
        return buffer.data()
    }
    public func accountID() async throws -> String {
        struct About: Decodable { struct User: Decodable { let permissionId: String }; let user: User }
        let about = try JSONDecoder().decode(About.self, from: await data("/drive/v3/about", query: [.init(name: "fields", value: "user(permissionId)")]))
        try Self.identifier(about.user.permissionId)
        return about.user.permissionId
    }
    public func file(_ id: String) async throws -> GoogleDriveFile {
        try Self.identifier(id)
        let file = try JSONDecoder().decode(GoogleDriveFile.self, from: await data("/drive/v3/files/" + id, query: [.init(name: "fields", value: fields)]))
        guard file.id == id else { throw GoogleDriveError.invalidMetadata }
        return file
    }
    private func list(query: String) async throws -> [GoogleDriveFile] {
        struct Page: Decodable { let files: [GoogleDriveFile]; let nextPageToken: String?; let incompleteSearch: Bool? }
        var result: [GoogleDriveFile] = [], page: String?, seen: Set<String> = [], ids: Set<String> = []
        for _ in 0..<100 {
            var items: [URLQueryItem] = [.init(name: "q", value: query), .init(name: "pageSize", value: "1000"),
                .init(name: "spaces", value: "drive"), .init(name: "fields", value: "nextPageToken,incompleteSearch,files(\(fields))")]
            if let page { items.append(.init(name: "pageToken", value: page)) }
            let response = try JSONDecoder().decode(Page.self, from: await data("/drive/v3/files", query: items))
            guard response.incompleteSearch != true, response.files.count <= 1000 else { throw GoogleDriveError.limit }
            for file in response.files {
                try Self.identifier(file.id)
                guard ids.insert(file.id).inserted else { throw GoogleDriveError.conflict }
                result.append(file)
            }
            guard result.count <= 50_000 else { throw GoogleDriveError.limit }
            guard let next = response.nextPageToken, !next.isEmpty else { return result }
            guard next.utf8.count <= 4096, seen.insert(next).inserted else { throw GoogleDriveError.limit }
            page = next
        }
        throw GoogleDriveError.limit
    }
    public func listRoots() async throws -> [GoogleDriveFile] {
        let roots = try await list(query: "trashed = false and mimeType = '\(Self.folderMIME)' and properties has { key='vemoProtocol' and value='1' } and properties has { key='vemoKind' and value='root' }")
        var names: Set<String> = [], processors: Set<String> = []
        for root in roots {
            try Self.validateRoot(root)
            guard names.insert(root.name).inserted,
                  let processor = root.properties?["vemoProcessor"], processors.insert(processor).inserted else { throw GoogleDriveError.conflict }
        }
        return roots
    }
    static func validateRoot(_ file: GoogleDriveFile, processorID: String? = nil) throws {
        guard file.trashed == false, file.mimeType == folderMIME, !file.name.isEmpty,
              let processor = file.properties?["vemoProcessor"],
              file.properties == ["vemoProtocol": "1", "vemoKind": "root", "vemoProcessor": processor] else { throw GoogleDriveError.invalidMetadata }
        try MobileSyncValidation.uuid(processor)
        if let processorID, processorID != processor { throw GoogleDriveError.conflict }
    }
    func children(_ parent: String) async throws -> [GoogleDriveFile] {
        try Self.identifier(parent)
        return try await list(query: "'\(parent)' in parents and trashed = false")
    }
    static func properties(path: String) -> [String: String] {
        ["vemoProtocol": "1", "vemoKey": MobileSyncValidation.digest(Data(path.utf8))]
    }
    static func validate(_ file: GoogleDriveFile, parent: String, path: String, mime: String) throws {
        guard file.trashed == false, file.parents == [parent], file.name == path.split(separator: "/").last.map(String.init),
              file.mimeType == mime, file.properties == properties(path: path) else { throw GoogleDriveError.invalidMetadata }
    }
    static func checkPublication(_ shouldPublish: @Sendable () async -> Bool) async throws {
        try Task.checkCancellation()
        guard await shouldPublish() else { throw GoogleDriveError.staleJob }
        try Task.checkCancellation()
    }
    func allocateID() async throws -> String {
        struct IDs: Decodable { let ids: [String] }
        let ids = try JSONDecoder().decode(IDs.self, from: await data("/drive/v3/files/generateIds", query: [.init(name: "count", value: "1"), .init(name: "space", value: "drive"), .init(name: "type", value: "files")])).ids
        guard ids.count == 1 else { throw GoogleDriveError.invalidResponse }
        try Self.identifier(ids[0]); return ids[0]
    }
    func create(id: String, name: String, parent: String?, mime: String, properties: [String: String],
                shouldPublish: (@Sendable () async -> Bool)? = nil) async throws -> GoogleDriveFile {
        try Self.identifier(id)
        var object: [String: Any] = ["id": id, "name": name, "mimeType": mime, "properties": properties]
        if let parent { try Self.identifier(parent); object["parents"] = [parent] }
        return try JSONDecoder().decode(GoogleDriveFile.self, from: await data("/drive/v3/files", query: [.init(name: "fields", value: fields)], method: "POST", body: JSONSerialization.data(withJSONObject: object), shouldPublish: shouldPublish))
    }
    func upload(id: String, data body: Data, shouldPublish: (@Sendable () async -> Bool)? = nil) async throws {
        try Self.identifier(id)
        guard body.count <= Self.jsonLimit else { throw GoogleDriveError.limit }
        _ = try await data("/upload/drive/v3/files/" + id, query: [.init(name: "uploadType", value: "media")], method: "PATCH", body: body, shouldPublish: shouldPublish)
    }
    func download(id: String, maximumBytes: Int64, receive: @escaping @Sendable (Data) throws -> Void) async throws {
        try Self.identifier(id)
        guard maximumBytes > 0, maximumBytes <= MobileSyncFiles.maximumAudioBytes else { throw GoogleDriveError.limit }
        let request = try await request("/drive/v3/files/" + id, query: [.init(name: "alt", value: "media")])
        let status = try await transport.send(request, maximumBytes: maximumBytes, receive: receive)
        try Task.checkCancellation()
        guard status == 200 else { throw GoogleDriveError.http(status) }
    }
    func json(_ file: GoogleDriveFile) async throws -> Data {
        guard let text = file.size, let size = Int64(text), size > 0, size <= Self.jsonLimit else { throw GoogleDriveError.limit }
        let buffer = GoogleDriveBuffer(limit: size)
        try await download(id: file.id, maximumBytes: size) { try buffer.append($0) }
        let data = buffer.data()
        guard data.count == size else { throw GoogleDriveError.invalidResponse }
        return data
    }
}
