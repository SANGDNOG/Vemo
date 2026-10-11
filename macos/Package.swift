// swift-tools-version: 6.0
import PackageDescription
let package = Package(name: "VoiceMemoTranscriber", platforms: [.macOS(.v14)], products: [.executable(name: "VoiceMemoTranscriber", targets: ["VoiceMemoTranscriber"]), .executable(name: "VemoAudio", targets: ["VemoAudio"])], targets: [
    .systemLibrary(name: "CSQLite"),
    .target(name: "NativeCore", dependencies: ["CSQLite"]),
    .executableTarget(name: "VemoAudio", dependencies: ["NativeCore"]),
    .executableTarget(name: "VoiceMemoTranscriber", dependencies: ["NativeCore"]),
    .executableTarget(name: "NativeChecks", dependencies: ["NativeCore", "CSQLite"], path: "Tests/NativeChecks"),
    .testTarget(name: "NativeCoreTests", dependencies: ["NativeCore"])
])
