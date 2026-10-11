import Foundation

public enum RuntimePaths {
    public static func stateRoot(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        let support = home.appendingPathComponent("Library/Application Support")
        let legacy = support.appendingPathComponent("VoiceMemoTranscriber")
        return FileManager.default.fileExists(atPath: legacy.path) ? legacy : support.appendingPathComponent("Vemo")
    }

    public static func python(root: URL, bundle: Bundle = .main) -> URL {
        let bundled = bundle.bundleURL.appendingPathComponent("Contents/Resources/runtime/bin/python3")
        return FileManager.default.isExecutableFile(atPath: bundled.path) ? bundled : root.appendingPathComponent("runtime/bin/python3")
    }

    public static func decoder(bundle: Bundle = .main) -> URL {
        let bundled = bundle.bundleURL.appendingPathComponent("Contents/MacOS/VemoAudio")
        return FileManager.default.isExecutableFile(atPath: bundled.path) ? bundled : URL(fileURLWithPath: "/opt/homebrew/bin/ffmpeg")
    }
}
