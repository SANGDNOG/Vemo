import XCTest
@testable import NativeCore
final class WireTests: XCTestCase {
    func testProtocolAndSequenceRejected() throws {
        for line in [#"{"sequence":1,"protocol_version":2,"kind":"snapshot","payload":{}}"#, #"{"sequence":1,"protocol_version":1,"kind":"snapshot","payload":{}}"#] { XCTAssertThrowsError(try WireCodec.decode(Data(line.utf8), after: 1)) }
    }
    func testVersionOneAndSnakeCaseCommand() throws {
        let data = try JSONEncoder().encode(WireCommand("hello"))
        let object = try JSONSerialization.jsonObject(with: data) as! [String:Any]
        XCTAssertEqual(object["protocol_version"] as? Int, 1); XCTAssertNotNil(object["request_id"])
        XCTAssertEqual(try WireCodec.decode(Data(#"{"sequence":2,"protocol_version":1,"kind":"snapshot","payload":{}}"#.utf8), after: 1).kind, "snapshot")
    }
    func testLargeLineRejected() { XCTAssertThrowsError(try WireCodec.decode(Data(repeating: 32, count: WireCodec.limit + 1), after: 0)) }
}
