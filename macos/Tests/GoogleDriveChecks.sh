#!/bin/bash
# CLT-only fallback: compile the same synthetic XCTest cases with tiny assertion
# helpers. No network calls or credentials. Artifacts live only in mktemp scratch.
set -euo pipefail
cd "$(dirname "$0")/../.."
task_scratch=$(mktemp -d /tmp/vemo-drive-checks.XXXXXX)
trap 'rm -rf "$task_scratch"' EXIT
python3 - "$task_scratch/GoogleDriveChecks.swift" <<'PY'
import sys
from pathlib import Path
source = Path('macos/Tests/NativeCoreTests/GoogleDriveTests.swift').read_text()
source = source.replace('import XCTest', 'import Foundation').replace('@testable import NativeCore', '')
source = source.replace('final class GoogleDriveTests: XCTestCase, @unchecked Sendable', 'final class GoogleDriveTests: XCTestCase, @unchecked Sendable')
shim = '''
class XCTestCase: @unchecked Sendable {
    var cleanups: [@Sendable () -> Void] = []
    func addTeardownBlock(_ action: @escaping @Sendable () -> Void) { cleanups.append(action) }
    func cleanup() { for action in cleanups { action() }; cleanups = [] }
}
func XCTFail(_ message: String) { fatalError(message) }
func XCTAssertTrue(_ value: Bool) { precondition(value) }
func XCTAssertFalse(_ value: Bool) { precondition(!value) }
func XCTAssertNil<T>(_ value: T?) { precondition(value == nil) }
func XCTAssertEqual<T: Equatable>(_ first: T, _ second: T) { precondition(first == second, "Mismatch: \\(first) != \\(second)") }
func XCTAssertThrowsError<T>(_ expression: @autoclosure () throws -> T) {
    do { _ = try expression(); fatalError("Expected error") } catch {}
}
@main struct GoogleDriveChecks {
    static func main() async throws {
        let tests = GoogleDriveTests()
        defer { tests.cleanup() }
        print("testInitialRestoreAndImmutableBinding")
        try await tests.testInitialRestoreAndImmutableBinding()
        print("testWrongAccountFailsBeforeRootLookup")
        try await tests.testWrongAccountFailsBeforeRootLookup()
        print("testRootPaginationAndDuplicateNamesFailClosed")
        try await tests.testRootPaginationAndDuplicateNamesFailClosed()
        print("testRepeatedPaginationTokenAndHTTPFailures")
        try await tests.testRepeatedPaginationTokenAndHTTPFailures()
        print("testMetadataExactnessAndPathKey")
        try tests.testMetadataExactnessAndPathKey()
        print("testJSONBoundAndShortBody")
        try await tests.testJSONBoundAndShortBody()
        print("testSymlinkStateRejected")
        try await tests.testSymlinkStateRejected()
        print("testLostCreateAcknowledgementReusesPersistedID")
        try await tests.testLostCreateAcknowledgementReusesPersistedID()
        print("testPullCommitsAudioBeforeMarkerAndRejectsBadHash")
        try await tests.testPullCommitsAudioBeforeMarkerAndRejectsBadHash()
        try await tests.testCancellationAndInvalidTokenDoNotSendRequests()
        try await tests.testWrongProcessorPrepareDoesNotMutateRemoteOrRebindBroker()
        try await tests.testFinalLiveGuardRejectsStaleJobWithoutUpload()
        try await tests.testMutationGuardRunsAfterTokenAwait()
        try await tests.testGooglePrepareAcceptsCanonicalInternalCacheAndRejectsOtherOverlap()
        try await tests.testRootPaginationRejectsDuplicateProcessorAcrossDifferentNames()
        try await tests.testForeignNameCollisionJournalsDistinctRootAndResumesLostCreate()
        try await tests.testRootIntentRejectsWrongIdentityAndUnrelatedName()
        try await tests.testFallbackNameCollisionFailsBeforeAllocatingOrWriting()
        try await tests.testExistingProcessorRootFailsBeforeAllocationEvenWithDifferentName()
        print("GoogleDrive: 19 synthetic checks passed")
    }
}
'''
Path(sys.argv[1]).write_text(source + shim)
PY
swiftc -swift-version 6 -parse-as-library -module-name GoogleDriveChecks \
  -I macos/Sources/CSQLite macos/Sources/NativeCore/*.swift \
  "$task_scratch/GoogleDriveChecks.swift" -o "$task_scratch/checks"
NSUnbufferedIO=YES "$task_scratch/checks"
