import Foundation
import NativeCore

func runtimePathChecks() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("Vemo paths with spaces " + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let support = root.appendingPathComponent("Library/Application Support")
    try check(RuntimePaths.stateRoot(home: root) == support.appendingPathComponent("Vemo"), "new state uses Vemo")
    let legacy = support.appendingPathComponent("VoiceMemoTranscriber")
    try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
    let sentinel = legacy.appendingPathComponent("existing-state"); try Data("unchanged".utf8).write(to: sentinel)
    let preserved = try Data(contentsOf: sentinel)
    try check(RuntimePaths.stateRoot(home: root).path == legacy.path && preserved == Data("unchanged".utf8), "legacy state reused without migration")
    let app = root.appendingPathComponent("Relocated Vemo.app")
    let contents = app.appendingPathComponent("Contents")
    try FileManager.default.createDirectory(at: contents.appendingPathComponent("Resources/runtime/bin"), withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: contents.appendingPathComponent("MacOS"), withIntermediateDirectories: true)
    try Data(#"<?xml version="1.0"?><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>io.test.vemo.paths</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleExecutable</key><string>Vemo</string></dict></plist>"#.utf8).write(to: contents.appendingPathComponent("Info.plist"))
    let bundle = Bundle(url: app)!
    let python = contents.appendingPathComponent("Resources/runtime/bin/python3")
    let decoder = contents.appendingPathComponent("MacOS/VemoAudio")
    try check(RuntimePaths.python(root: legacy, bundle: bundle) == legacy.appendingPathComponent("runtime/bin/python3"), "missing bundled Python falls back")
    try check(RuntimePaths.decoder(bundle: bundle).path == "/opt/homebrew/bin/ffmpeg", "missing decoder keeps developer FFmpeg")
    for file in [python, decoder] { try Data("#!/bin/sh\nexit 0\n".utf8).write(to: file); try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path) }
    try check(RuntimePaths.python(root: legacy, bundle: bundle) == python, "bundled Python resolves relocated spaces")
    try check(RuntimePaths.decoder(bundle: bundle) == decoder, "bundled native decoder resolves relocated spaces")
    try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: python.path)
    try check(RuntimePaths.python(root: legacy, bundle: bundle) == legacy.appendingPathComponent("runtime/bin/python3"), "nonexecutable bundled interpreter falls back")
}
