using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SherpaOnnx;
using Vemo.Core;
using Vemo.Diarization;

try
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows speaker helper only.");
    if (args.Length < 3) throw new ArgumentException("Vemo.Diarization <16k-mono-pcm.wav> <pinned-model-directory> <new-turns.json> [--threshold <0..2 exclusive of 0>] [--num-clusters <1..20>]");
    var probe = SpeakerProbeOptions.Parse(args.Skip(3).ToArray());
    NativeRuntime.Initialize();
    SpeakerModels.Verify(args[1]);
    var memory = new MemoryStatus(); memory.Length = (uint)Marshal.SizeOf<MemoryStatus>();
    if (!Program.GlobalMemoryStatusEx(ref memory) || memory.AvailablePhysical < 4UL * 1024 * 1024 * 1024) throw new IOException("화자 분석에는 4 GiB 이상의 여유 메모리가 필요합니다.");
    SafeFiles.NoLinks(args[0]); SafeFiles.NoLinks(args[2]);
    // Validate the complete PCM container before allocating the float sample array.
    float[] samples;
    using (var stream = File.OpenRead(args[0])) using (var r = new BinaryReader(stream))
    {
        if (new string(r.ReadChars(4)) != "RIFF" || (long)r.ReadUInt32() + 8 != stream.Length) throw new InvalidDataException("PCM WAV required.");
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("PCM WAV required.");
        bool format = false, data = false; long dataOffset = 0; uint dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(r.ReadChars(4)); var size = r.ReadUInt32(); var next = stream.Position + size + (size & 1);
            if (next > stream.Length) throw new InvalidDataException("Truncated WAV.");
            if (id == "fmt ") { if (format || size < 16 || r.ReadUInt16() != 1 || r.ReadUInt16() != 1 || r.ReadUInt32() != 16000 || r.ReadUInt32() != 32000 || r.ReadUInt16() != 2 || r.ReadUInt16() != 16) throw new InvalidDataException("16 kHz mono PCM16 required."); format = true; }
            if (id == "data") { if (data || !format || size == 0 || (size & 1) != 0 || size > 3600L * 32000) throw new InvalidDataException("화자 분석은 최대 60분까지 지원합니다."); data = true; dataOffset = stream.Position; dataBytes = size; }
            stream.Position = next;
        }
        if (!format || !data || stream.Position != stream.Length) throw new InvalidDataException("Missing or trailing WAV audio.");
        samples = new float[checked((int)(dataBytes / 2))]; stream.Position = dataOffset;
        for (int i = 0; i < samples.Length; i++) samples[i] = r.ReadInt16() / 32768f;
    }
    var config = new OfflineSpeakerDiarizationConfig();
    config.Segmentation.Pyannote.Model = Path.Combine(args[1], "segmentation.onnx"); config.Segmentation.Pyannote.WindowShiftRatio = 0.1f;
    config.Segmentation.Provider = "cpu"; config.Segmentation.NumThreads = 2;
    config.Embedding.Model = Path.Combine(args[1], "embedding.onnx"); config.Embedding.Provider = "cpu"; config.Embedding.NumThreads = 2;
    config.Clustering.NumClusters = probe.NumClusters; config.Clustering.Threshold = probe.Threshold; config.MinDurationOn = 0.3f; config.MinDurationOff = 0.5f;
    using var diarizer = new OfflineSpeakerDiarization(config);
    if (diarizer.SampleRate != 16000 || samples.Length > 3600L * 16000) throw new InvalidDataException("Invalid sample rate/duration.");
    var callback = new OfflineSpeakerDiarizationProgressCallback((done, total, _) => { if (total > 0) Console.WriteLine("VEMO_PROGRESS " + (100d * done / total).ToString("F2", CultureInfo.InvariantCulture)); return 0; });
    var turns = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);
    if (turns.Select(t => t.Speaker).Distinct().Take(21).Count() > 20) throw new InvalidDataException("화자가 20명을 초과했습니다. 화자 수를 지정해 다시 분석하세요.");
    if (turns.Length > 100000) throw new InvalidDataException("Too many speaker turns.");
    SafeFiles.Atomic(args[2], JsonSerializer.Serialize(turns.Select(t => SpeakerTurnBounds.Normalize(new SpeakerTurn(t.Start, t.End, "cluster_" + t.Speaker), samples.Length / 16000d))), false);
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

[StructLayout(LayoutKind.Sequential)]
struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
partial class Program
{
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
