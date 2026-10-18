using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Vemo.Core;
using Vemo.Provision;
namespace Vemo.Runtime;

public sealed record NativeProbe(int ExitCode, string Output);
public interface IRuntimeEnvironment
{
    bool SupportsCuda { get; }
    Task<string?> NvidiaAsync(CancellationToken ct);
    Task<NativeProbe> RunAsync(string executable, string[] arguments, CancellationToken ct);
}

public sealed class WindowsRuntimeEnvironment : IRuntimeEnvironment
{
    public bool SupportsCuda => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 && RuntimeInformation.OSArchitecture == Architecture.X64;
    public async Task<string?> NvidiaAsync(CancellationToken ct)
    {
        if (!SupportsCuda) return null;
        var paths = new[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe") };
        var exe = paths.FirstOrDefault(File.Exists); if (exe == null) return null;
        PinnedDownloads.RequirePe(exe, "x64");
        var result = await RunAsync(exe, ["--query-gpu=name,driver_version", "--format=csv,noheader"], ct);
        return result.ExitCode == 0 && Regex.IsMatch(result.Output.Trim(), @"(?m)^[^,\r\n]{1,200},\s*\d+(?:\.\d+)+\s*$") ? result.Output.Trim() : null;
    }
    public async Task<NativeProbe> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        // Only installed siblings and Windows system libraries participate in dependency lookup.
        info.Environment["PATH"] = Path.GetDirectoryName(executable) + Path.PathSeparator + Environment.SystemDirectory;
        using var process = new Process { StartInfo = info }; process.Start();
        async Task<string> Read(StreamReader reader)
        {
            var buffer = new char[4096]; var output = new StringBuilder(); int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0) { if (output.Length + n > 128 * 1024) throw new IOException("GPU probe output exceeded limit."); output.Append(buffer, 0, n); }
            return output.ToString();
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try { await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr); return new(process.ExitCode, await stdout + "\n" + await stderr); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("GPU device probe timed out (30 seconds)."); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}

public sealed class RuntimePreparation : IRuntimePreparation
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    readonly string root; readonly PinnedDownloads downloads; readonly IRuntimeEnvironment environment; readonly string catalogue;
    public string Status { get; private set; } = "자동 GPU 준비 대기";
    public RuntimePreparation(string root, HttpClient http, IRuntimeEnvironment? environment = null, string? catalogue = null)
    {
        this.root = SafeFiles.Full(root); downloads = new(http); this.environment = environment ?? new WindowsRuntimeEnvironment(); this.catalogue = catalogue ?? Path.Combine(AppContext.BaseDirectory, "runtime-catalogue.json");
    }
    RuntimePreparationResult Cpu(ModelSelection model, string message, bool ready = true)
    {
        Status = message;
        return new(model with { Compute = "CPU", AutoGpuLayers = false, CudaConfirmed = false, RuntimeDevice = "CPU",
            WhisperExecutable = string.IsNullOrEmpty(model.CpuWhisperExecutable) ? model.WhisperExecutable : model.CpuWhisperExecutable,
            LlamaExecutable = string.IsNullOrEmpty(model.CpuLlamaExecutable) ? model.LlamaExecutable : model.CpuLlamaExecutable }, message, ready);
    }
    public async Task<RuntimePreparationResult> PrepareAsync(ModelSelection model, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (model.ComputePreference == "CPU") return Cpu(model, "CPU · 직접 선택");
        if (model.ComputePreference is not ("Auto" or "CUDA")) throw new InvalidDataException("Unknown compute preference.");
        if (!File.Exists(model.WhisperModel) || !File.Exists(model.LlamaModel)) return Cpu(model, "모델 파일 대기 · 설치한 모델 경로를 확인하세요.", false);
        if (!environment.SupportsCuda) return Cpu(model, "CPU · CUDA 자동 준비는 Windows x64 NVIDIA만 지원합니다 (ARM64/AMD/Intel 미지원).");
        var gate = Gates.GetOrAdd(root, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            Status = "NVIDIA 장치 확인 중…";
            var nvidia = await environment.NvidiaAsync(ct);
            if (nvidia == null) return Cpu(model, "CPU · 지원 NVIDIA 드라이버/장치를 찾지 못했습니다. AMD/Intel CUDA는 지원하지 않습니다.");
            SafeFiles.NoLinks(root); Directory.CreateDirectory(root);
            // Cross-process lease: a second process never touches an active setup's staging/cache.
            var leasePath = Path.Combine(root, "setup.lock"); SafeFiles.NoLinks(leasePath);
            await using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var doc = JsonFormat.Strict(SafeFiles.Read(catalogue));
            var entries = doc.RootElement.GetProperty("cuda_runtimes").EnumerateArray().ToArray();
            if (entries.Length != 3 || entries.Any(x => x.GetProperty("architecture").GetString() != "x64")) throw new InvalidDataException("Invalid CUDA runtime catalogue.");
            var pins = entries.ToDictionary(x => x.GetProperty("engine").GetString()!, DownloadPin.From);
            if (!pins.Keys.Order().SequenceEqual(new[] { "cudart", "llama", "whisper" })) throw new InvalidDataException("Incomplete CUDA pins.");
            var archives = new Dictionary<string, string>();
            foreach (var (engine, pin) in pins) { ct.ThrowIfCancellationRequested(); Status = "GPU 엔진 다운로드·무결성 확인 중 · " + engine; archives[engine] = await downloads.AcquireAsync(pin, Path.Combine(root, "cache"), false, ct); }
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", pins.OrderBy(x => x.Key).Select(x => x.Value.Sha256)))))[..20];
            var install = Path.Combine(root, "cuda-" + identity); SafeFiles.NoLinks(install);
            if (!Directory.Exists(install))
            {
                var stage = install + ".staging-" + Guid.NewGuid(); Directory.CreateDirectory(stage);
                try
                {
                    foreach (var (engine, archive) in archives) { ct.ThrowIfCancellationRequested(); PinnedDownloads.Extract(archive, Path.Combine(stage, engine), ct); }
                    InstallCudaDependencies(stage);
                    ValidatePe(stage); ct.ThrowIfCancellationRequested(); Directory.Move(stage, install);
                }
                finally { if (Directory.Exists(stage)) SafeFiles.DeleteTree(stage); }
            }
            Status = "설치한 GPU 엔진 검증 중…";
            foreach (var (engine, archive) in archives) await VerifyTree(archive, Path.Combine(install, engine), ct);
            VerifyCudaDependencies(install); VerifyNoExtraFiles(install, archives); ValidatePe(install);
            var whisper = Executable(install, "whisper"); var llama = Executable(install, "llama");
            Status = "CUDA backend 장치 확인 중…";
            var llamaProbe = await environment.RunAsync(llama, ["--list-devices"], ct);
            var device = Regex.Match(llamaProbe.Output, @"(?m)^\s*(CUDA0):\s*([^\r\n]+)");
            if (llamaProbe.ExitCode != 0 || !device.Success) return Cpu(model, "CPU · llama CUDA backend가 지원 장치를 확인하지 못했습니다.");
            // Existing tiny invalid files pass CLI file checks, initialize backends, then fail before any model allocation.
            var probeDirectory = Path.Combine(root, "probe-" + Guid.NewGuid()); Directory.CreateDirectory(probeDirectory);
            NativeProbe whisperProbe;
            try { var invalid = Path.Combine(probeDirectory, "invalid-model.bin"); await File.WriteAllBytesAsync(invalid, [0, 0, 0, 0], ct); whisperProbe = await environment.RunAsync(whisper, ["-m", invalid, "-f", invalid], ct); }
            finally { SafeFiles.DeleteTree(probeDirectory); }
            if (whisperProbe.ExitCode != 3 || !Regex.IsMatch(whisperProbe.Output, @"ggml_cuda_init: found [1-9]\d* CUDA devices") || !whisperProbe.Output.Contains("loaded CUDA backend", StringComparison.Ordinal)) return Cpu(model, "CPU · Whisper CUDA backend 장치 초기화를 확인하지 못했습니다.");
            ct.ThrowIfCancellationRequested();
            Status = "CUDA · " + device.Groups[1].Value + " · " + device.Groups[2].Value.Trim() + " · 자동 레이어 (여유 2048 MiB)";
            return new(model with { WhisperExecutable = whisper, LlamaExecutable = llama, Compute = "CUDA", CudaConfirmed = true, AutoGpuLayers = true, RuntimeDevice = device.Groups[1].Value + ": " + device.Groups[2].Value.Trim(),
                CpuWhisperExecutable = model.Compute == "CPU" ? model.WhisperExecutable : model.CpuWhisperExecutable, CpuLlamaExecutable = model.Compute == "CPU" ? model.LlamaExecutable : model.CpuLlamaExecutable }, Status, true);
        }
        catch (OperationCanceledException) { Status = "GPU 준비 취소 · 기존 설정 유지"; throw; }
        catch (InvalidDataException e) { Status = "GPU 무결성 확인 실패 · " + e.Message; throw; }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) { Status = "GPU 다운로드 접근 실패 · " + e.StatusCode; throw; }
        catch (Exception e) when (e is IOException or HttpRequestException or System.ComponentModel.Win32Exception) { return Cpu(model, "CPU · GPU 준비 실패: " + e.Message); }
        finally { gate.Release(); }
    }
    static string Executable(string root, string engine) => Directory.EnumerateFiles(Path.Combine(root, engine), engine == "whisper" ? "whisper-cli.exe" : "llama-cli.exe", SearchOption.AllDirectories).Single();
    static IEnumerable<string> CudaDependencies(string root) => Directory.EnumerateFiles(Path.Combine(root, "cudart"), "*.dll", SearchOption.AllDirectories);
    static void InstallCudaDependencies(string root)
    {
        var target = Path.GetDirectoryName(Executable(root, "llama"))!;
        foreach (var file in CudaDependencies(root)) { var dest = Path.Combine(target, Path.GetFileName(file)); if (File.Exists(dest)) { if (SafeFiles.Hash(file) != SafeFiles.Hash(dest)) throw new InvalidDataException("Conflicting CUDA dependency."); } else File.Copy(file, dest); }
    }
    static void VerifyCudaDependencies(string root)
    {
        var target = Path.GetDirectoryName(Executable(root, "llama"))!;
        var files = CudaDependencies(root).ToArray(); if (files.Length == 0) throw new InvalidDataException("Missing pinned CUDA dependencies.");
        foreach (var file in files) { var dest = Path.Combine(target, Path.GetFileName(file)); SafeFiles.NoLinks(dest); if (!File.Exists(dest) || SafeFiles.Hash(file) != SafeFiles.Hash(dest)) throw new InvalidDataException("Installed CUDA dependency differs from pinned archive."); }
    }
    static void ValidatePe(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(x => Path.GetExtension(x).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x).Equals(".dll", StringComparison.OrdinalIgnoreCase))) PinnedDownloads.RequirePe(file, "x64");
        _ = Executable(root, "whisper"); _ = Executable(root, "llama");
    }
    static void VerifyNoExtraFiles(string install, IReadOnlyDictionary<string, string> archives)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, archive) in archives)
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith('/')))
                expected.Add(Path.GetFullPath(Path.Combine(install, engine, entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))));
        }
        var target = Path.GetDirectoryName(Executable(install, "llama"))!;
        foreach (var file in CudaDependencies(install)) expected.Add(Path.Combine(target, Path.GetFileName(file)));
        foreach (var file in Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories))
        {
            SafeFiles.NoLinks(file);
            if (!expected.Contains(Path.GetFullPath(file))) throw new InvalidDataException("Unpinned extra file in GPU runtime installation.");
        }
    }
    static async Task VerifyTree(string archive, string directory, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith('/')))
        {
            ct.ThrowIfCancellationRequested(); var path = Path.Combine(directory, entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)); SafeFiles.NoLinks(path);
            if (!SafeFiles.Within(directory, path) || !File.Exists(path) || new FileInfo(path).Length != entry.Length) throw new InvalidDataException("Installed runtime differs from pinned archive.");
            await using var original = entry.Open(); await using var installed = File.OpenRead(path);
            var expected = await SHA256.HashDataAsync(original, ct); var actual = await SHA256.HashDataAsync(installed, ct);
            if (!expected.SequenceEqual(actual)) throw new InvalidDataException("Installed runtime hash differs from pinned archive.");
        }
    }
}
