import XCTest
@testable import NativeCore
final class ValidationTests: XCTestCase {
    func testLeapDateIsAccepted() throws { XCTAssertEqual(try Validation.recordingDate("2024-02-29"), "2024-02-29") }
    func testImpossibleDateAndTimestampAreRejected() { for value in ["2023-02-29", "2026-13-01", "2026-10-11T00:00:00", "2026-1-1"] { XCTAssertThrowsError(try Validation.recordingDate(value)) } }
    func testOwnedPathRejectsTraversalAndSymlinkEscape() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("ok"); try Data().write(to: file)
        XCTAssertEqual(try Validation.owned(file.path, root: root), file)
        XCTAssertThrowsError(try Validation.owned(root.appendingPathComponent("../outside").path, root: root))
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("escape"), withDestinationURL: URL(fileURLWithPath: "/tmp"))
        XCTAssertThrowsError(try Validation.owned(root.appendingPathComponent("escape/file").path, root: root))
    }
}
