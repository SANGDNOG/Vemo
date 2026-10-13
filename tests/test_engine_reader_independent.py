"""Synthetic native pipe regressions; never launches the app or reads user state."""
import json
import pathlib
import shutil
import subprocess
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]


@unittest.skipUnless(shutil.which("swift"), "Swift interpreter required")
class EngineReaderIndependentTests(unittest.TestCase):
    def run_reader_probe(self, body, observe_reader_exit=False):
        source = (ROOT / "macos/Sources/VoiceMemoTranscriber/EngineClient.swift").read_text()
        # Compile the actual production reader, with only its error type supplied
        # locally. No copied transport implementation or live EngineClient process.
        reader = source[:source.index("\nactor EngineClient")].replace("import NativeCore\n", "")
        if observe_reader_exit:
            # Observation only: count actual read scopes, including their exits.
            # A pull reader has no producer task while the consumer is paused;
            # every requested read must still retire promptly on cancellation.
            entry = "queue.async {"
            self.assertEqual(reader.count(entry), 1, "Adapt observer to the new reader entry point")
            reader = reader.replace(entry, entry + "\n independentReaderActivity.enter()\n defer { independentReaderActivity.leave() }", 1)
        script = reader + """
enum NativeFailure: Error { case invalid(String) }
final class IndependentReaderActivity: @unchecked Sendable {
    private let lock = NSLock()
    private var started = 0
    private var finished = 0
    func enter() { lock.lock(); started += 1; lock.unlock() }
    func leave() { lock.lock(); finished += 1; lock.unlock() }
    func snapshot() -> (started: Int, finished: Int, active: Int) {
        lock.lock(); defer { lock.unlock() }
        return (started, finished, started - finished)
    }
}
let independentReaderActivity = IndependentReaderActivity()
""" + body
        result = subprocess.run([shutil.which("swift"), "-"], input=script, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def probe(self, slow_consumer):
        script = """
let pipe = Pipe()
let stream = EnginePipeReader.chunks(pipe.fileHandleForReading, label: "independent.synthetic.reader")
let frames = (0..<40).map { Data(repeating: UInt8($0), count: 128) }
let expected = frames.reduce(into: Data()) { $0.append($1) }
DispatchQueue.global().async {
    for frame in frames {
        try? pipe.fileHandleForWriting.write(contentsOf: frame)
        usleep(2000)
    }
    try? pipe.fileHandleForWriting.close()
}
var received = Data()
var chunks = 0
var failure: String? = nil
do {
    for try await data in stream {
        received.append(data)
        chunks += 1
        if SLOW_CONSUMER && chunks == 1 {
            try? await Task.sleep(nanoseconds: 150_000_000)
        }
    }
} catch { failure = String(describing: error) }
let result: [String: Any] = ["bytes": received.count, "expected_bytes": expected.count,
                           "exact": received == expected, "error": failure ?? ""]
let encoded = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
print(String(decoding: encoded, as: UTF8.self))
try? pipe.fileHandleForReading.close()
"""
        script = script.replace("SLOW_CONSUMER", "true" if slow_consumer else "false")
        return self.run_reader_probe(script)

    def cancellation_probe(self, fill_capacity):
        body = """
let pipe = Pipe()
let stream = EnginePipeReader.chunks(pipe.fileHandleForReading, label: "independent.synthetic.cancel")
let consumerDone = DispatchSemaphore(value: 0)
let consumer = Task.detached {
    defer { consumerDone.signal() }
    do {
        for try await _ in stream {
            if FILL_CAPACITY { try? await Task.sleep(nanoseconds: 10_000_000_000) }
        }
    } catch {}
}
var capacityReached = false
var acceptedBytes = 0
if FILL_CAPACITY {
    // Nonblocking writes fill both transport and kernel buffers without leaving
    // a test writer stuck when cancellation stops the production reader.
    let fd = pipe.fileHandleForWriting.fileDescriptor
    let flags = fcntl(fd, F_GETFL)
    _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
    let bytes = [UInt8](repeating: 7, count: 65_536)
    var consecutiveFull = 0
    let deadline = Date().addingTimeInterval(3)
    while Date() < deadline && acceptedBytes < 32 * 1024 * 1024 {
        let written = bytes.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
        if written > 0 { acceptedBytes += written; consecutiveFull = 0 }
        else if written < 0 && (errno == EAGAIN || errno == EWOULDBLOCK) {
            consecutiveFull += 1
            if consecutiveFull >= 20 { capacityReached = true; break }
            usleep(5000)
        } else { break }
    }
} else {
    // Keep the writer open and silent: EOF must not be what releases the read.
    try? await Task.sleep(nanoseconds: 100_000_000)
}
let before = independentReaderActivity.snapshot()
consumer.cancel()
let consumerStopped = consumerDone.wait(timeout: .now() + 1) == .success
let exitDeadline = Date().addingTimeInterval(1)
while independentReaderActivity.snapshot().active != 0 && Date() < exitDeadline {
    try? await Task.sleep(nanoseconds: 10_000_000)
}
let after = independentReaderActivity.snapshot()
let result: [String: Any] = ["capacity_reached": capacityReached, "accepted_bytes": acceptedBytes,
                           "reads_started_before_cancel": before.started,
                           "active_reads_before_cancel": before.active,
                           "consumer_stopped": consumerStopped,
                           "reader_stopped": after.active == 0 && after.started == after.finished]
let encoded = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
print(String(decoding: encoded, as: UTF8.self))
// Cleanup happens only after observations, so it cannot manufacture a pass.
try? pipe.fileHandleForWriting.close()
try? pipe.fileHandleForReading.close()
""".replace("FILL_CAPACITY", "true" if fill_capacity else "false")
        return self.run_reader_probe(body, observe_reader_exit=True)

    def test_fast_consumer_receives_every_byte_in_order(self):
        result = self.probe(False)
        self.assertEqual(result["error"], "")
        self.assertEqual(result["bytes"], 5120)
        self.assertTrue(result["exact"])

    def test_slow_event_consumer_is_lossless_for_small_burst(self):
        result = self.probe(True)
        self.assertEqual(result["error"], "", result)
        self.assertEqual(result["bytes"], 5120)
        self.assertTrue(result["exact"])

    def test_cancel_releases_producer_waiting_for_capacity(self):
        result = self.cancellation_probe(True)
        self.assertTrue(result["capacity_reached"], result)
        self.assertGreater(result["accepted_bytes"], 0)
        self.assertGreaterEqual(result["reads_started_before_cancel"], 1, result)
        self.assertEqual(result["active_reads_before_cancel"], 0, result)
        self.assertTrue(result["consumer_stopped"], result)
        self.assertTrue(result["reader_stopped"], result)

    def test_cancel_releases_reader_blocked_on_empty_pipe(self):
        result = self.cancellation_probe(False)
        self.assertEqual(result["active_reads_before_cancel"], 1, result)
        self.assertTrue(result["consumer_stopped"], result)
        self.assertTrue(result["reader_stopped"], result)

    def test_shutdown_closes_stdin_without_blocking_write_before_cleanup(self):
        # This checks the shipping actor rather than a stand-in shutdown method.
        # Once output readers stop, a child with full pipes cannot be relied on to
        # consume one more command. EOF must precede supervised process cleanup.
        source = (ROOT / "macos/Sources/VoiceMemoTranscriber/EngineClient.swift").read_text()
        shutdown = source.split("func shutdown() async -> Bool {", 1)[1]
        before_cleanup = shutdown.split("ProcessCleanup.stop(", 1)
        self.assertEqual(len(before_cleanup), 2, "Shipping shutdown must reach supervised cleanup")
        self.assertIn("input?.close()", before_cleanup[0])
        self.assertNotRegex(before_cleanup[0], r"\bsend\s*\(|\.\s*write\s*\(",
                            "Shutdown must not wait for child stdin capacity before cleanup")
