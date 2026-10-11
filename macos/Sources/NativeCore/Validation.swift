import Foundation
public enum NativeFailure: Error, LocalizedError, Sendable {
    case invalid(String)
    public var errorDescription: String? { if case .invalid(let text) = self { return text }; return nil }
}
public enum Validation {
    public static func recordingDate(_ value: String) throws -> String {
        guard value.range(of: "^[0-9]{4}-[0-9]{2}-[0-9]{2}$", options: .regularExpression) != nil else { throw NativeFailure.invalid("녹음 시작일을 YYYY-MM-DD 형식으로 입력하세요.") }
        let f = DateFormatter(); f.calendar = Calendar(identifier: .gregorian); f.locale = Locale(identifier: "en_US_POSIX"); f.timeZone = TimeZone(secondsFromGMT: 0); f.dateFormat = "yyyy-MM-dd"; f.isLenient = false
        guard let d = f.date(from: value), f.string(from: d) == value else { throw NativeFailure.invalid("유효하지 않은 녹음일입니다.") }; return value
    }
    public static func component(_ value: String) throws -> String {
        guard !value.isEmpty, value != ".", value != "..", !value.contains("/"), !value.contains("\\"), !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { throw NativeFailure.invalid("안전하지 않은 파일 이름입니다.") }; return value
    }
    public static func owned(_ path: String, root: URL) throws -> URL {
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let canonicalRoot = root.standardizedFileURL.resolvingSymlinksInPath()
        guard url.path.hasPrefix(root.standardizedFileURL.path + "/"), url.resolvingSymlinksInPath().path.hasPrefix(canonicalRoot.path + "/") else { throw NativeFailure.invalid("작업 폴더 밖의 경로입니다.") }
        var walk = url
        while walk.path != root.standardizedFileURL.path {
            if let values = try? walk.resourceValues(forKeys: [.isSymbolicLinkKey]), values.isSymbolicLink == true { throw NativeFailure.invalid("심볼릭 링크는 허용하지 않습니다.") }
            walk.deleteLastPathComponent()
        }
        return url
    }
}
