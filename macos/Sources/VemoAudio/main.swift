import Foundation
import NativeCore

@main struct VemoAudio {
    static func main() async {
        let args = CommandLine.arguments
        guard args.count == 4, args[1] == "normalize", args[2].hasPrefix("/"), args[3].hasPrefix("/") else {
            try? FileHandle.standardError.write(contentsOf: Data("usage: VemoAudio normalize /input /output.wav\n".utf8))
            exit(2)
        }
        do {
            try await AudioNormalization.normalize(input: URL(fileURLWithPath: args[2]), output: URL(fileURLWithPath: args[3]))
        } catch {
            // Neither source paths nor decoded recording content enter diagnostic output.
            try? FileHandle.standardError.write(contentsOf: Data("audio_normalization_failed\n".utf8))
            exit(1)
        }
    }
}
