using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Vemo.Core;
using Vemo.Runtime;
using Xunit;
namespace Vemo.Provision.Tests;

public sealed class RuntimePreparationTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-auto-" + Guid.NewGuid());
    readonly FakeEnvironment env = new();
    readonly Dictionary<string, byte[]> archives = [];
    readonly HttpClient http;
    readonly string catalogue;
    int downloads;
    public RuntimePreparationTests()
    {
        Directory.CreateDirectory(root); catalogue = Path.Combine(root, "catalogue.json");
        foreach (var engine in new[] { "whisper", "llama", "cudart" })
        {
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry(engine == "cudart" ? "cudart64_12.dll" : "Release/" + engine + "-cli.exe");
                using (var output = entry.Open()) output.Write(Pe());
                if (engine != "cudart") { var cuda = zip.CreateEntry("Release/ggml-cuda.dll"); using var dll = cuda.Open(); dll.Write(Pe()); }
            }
            archives[engine + ".zip"] = data.ToArray();
        }
        File.WriteAllText(catalogue, JsonSerializer.Serialize(new { cuda_runtimes = archives.Select(x => new { file = x.Key, url = "https://github.com/ggml-org/test/releases/download/pinned/" + x.Key, size = x.Value.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(x.Value)), architecture = "x64", engine = Path.GetFileNameWithoutExtension(x.Key) }) }));
        http = new(new Handler(request => { downloads++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archives[Path.GetFileName(request.RequestUri!.AbsolutePath)]), RequestMessage = request }; }));
    }
    static byte[] Pe() { var bytes = new byte[128]; bytes[0] = 0x4d; bytes[1] = 0x5a; bytes[60] = 64; bytes[64] = 0x50; bytes[65] = 0x45; bytes[68] = 0x64; bytes[69] = 0x86; return bytes; }
    ModelSelection Model()
    {
        File.WriteAllText(Path.Combine(root, "asr.bin"), "model"); File.WriteAllText(Path.Combine(root, "27b.gguf"), "model");
        return new() { WhisperModel = Path.Combine(root, "asr.bin"), LlamaModel = Path.Combine(root, "27b.gguf"), NotesProfile = "Qwen3.5 27B GGUF", WhisperExecutable = "original-whisper.exe", LlamaExecutable = "original-llama.exe" };
    }
    RuntimePreparation Service() => new(Path.Combine(root, "runtime"), http, env, catalogue);
    [Fact] public async Task AutoLegacyCpuInstallsVerifiesAndSelectsActualDeviceWithoutChangingModel()
    {
        var model = Model(); var result = await Service().PrepareAsync(model);
        Assert.True(result.Ready); Assert.Equal("CUDA", result.Models.Compute); Assert.True(result.Models.AutoGpuLayers); Assert.True(result.Models.CudaConfirmed);
        Assert.Equal(model.LlamaModel, result.Models.LlamaModel); Assert.Equal(model.NotesProfile, result.Models.NotesProfile); Assert.Equal(model.WhisperModel, result.Models.WhisperModel);
        Assert.Contains("CUDA0", result.Models.RuntimeDevice); Assert.Equal(3, downloads); Assert.Equal(2, env.Probes);
        Assert.Equal(model.WhisperExecutable, result.Models.CpuWhisperExecutable);
        var cpu = await Service().PrepareAsync(result.Models with { ComputePreference = "CPU" });
        Assert.Equal("CPU", cpu.Models.Compute); Assert.Equal(model.WhisperExecutable, cpu.Models.WhisperExecutable); Assert.False(cpu.Models.AutoGpuLayers); Assert.Equal(3, downloads);
    }
    [Fact] public async Task ExplicitCpuMissingModelsAndUnsupportedHardwareNeverDownload()
    {
        Assert.Equal("CPU", (await Service().PrepareAsync(new() { ComputePreference = "CPU", Compute = "CUDA", AutoGpuLayers = true })).Models.Compute);
        Assert.False((await Service().PrepareAsync(new())).Ready);
        env.SupportsCuda = false; Assert.Equal("CPU", (await Service().PrepareAsync(Model())).Models.Compute);
        env.SupportsCuda = true; env.Nvidia = null; Assert.Contains("NVIDIA", (await Service().PrepareAsync(Model())).Status);
        Assert.Equal(0, downloads); Assert.Equal(0, env.Probes);
    }
    [Theory][InlineData(false, true)][InlineData(true, false)]
    public async Task HardwareAloneCannotClaimCuda(bool llama, bool whisper)
    {
        env.Llama = llama; env.Whisper = whisper; var result = await Service().PrepareAsync(Model());
        Assert.Equal("CPU", result.Models.Compute); Assert.False(result.Models.CudaConfirmed); Assert.Contains("backend", result.Status);
    }
    [Fact] public async Task BadArchiveHashIsSurfacedAndNoExecutableRuns()
    {
        archives["whisper.zip"][0] ^= 1;
        var service = Service(); await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(Model()));
        Assert.Contains("무결성", service.Status); Assert.Equal(0, env.Probes); Assert.Empty(Directory.GetDirectories(Path.Combine(root, "runtime"), "cuda-*"));
    }
    [Fact] public async Task InstalledTamperingOrExtraDllIsRejectedBeforeExecution()
    {
        var service = Service(); var model = Model(); var result = await service.PrepareAsync(model); env.Probes = 0;
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(result.Models.LlamaExecutable)!, "injected.dll"), Pe());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(model)); Assert.Equal(0, env.Probes);
        File.Delete(Path.Combine(Path.GetDirectoryName(result.Models.LlamaExecutable)!, "injected.dll")); File.WriteAllBytes(result.Models.WhisperExecutable, Pe().Concat(new byte[] { 1 }).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(model)); Assert.Equal(0, env.Probes);
    }
    [Fact] public async Task CancellationReleasesSetupAndDoesNotChangeInputSnapshot()
    {
        var model = Model(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().PrepareAsync(model, cancel.Token));
        Assert.Equal("CPU", model.Compute); Assert.Equal("CUDA", (await Service().PrepareAsync(model)).Models.Compute);
    }
    [Fact] public async Task ConcurrentPreparationSharesVerifiedInstallation()
    {
        var model = Model(); var results = await Task.WhenAll(Service().PrepareAsync(model), Service().PrepareAsync(model));
        Assert.All(results, r => Assert.Equal("CUDA", r.Models.Compute)); Assert.Equal(results[0].Models.WhisperExecutable, results[1].Models.WhisperExecutable); Assert.Equal(3, downloads);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "runtime"), "*.staging-*"));
    }
    [Fact] public async Task NetworkFailureFallsBackButAccessDenialIsNotSilentlyHidden()
    {
        var model = Model();
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        var service = new RuntimePreparation(Path.Combine(root, "offline-runtime"), offline, env, catalogue);
        var result = await service.PrepareAsync(model); Assert.Equal("CPU", result.Models.Compute); Assert.Contains("offline", result.Status); Assert.Equal(0, env.Probes);
        using var denied = new HttpClient(new Handler(request => new(HttpStatusCode.Forbidden) { RequestMessage = request }));
        service = new RuntimePreparation(Path.Combine(root, "denied-runtime"), denied, env, catalogue);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PrepareAsync(model)); Assert.Contains("접근 실패", service.Status); Assert.Equal(0, env.Probes);
    }
    [Fact] public async Task CancellationDuringNativeProbeKeepsSnapshotAndCleansDiagnosticFiles()
    {
        var model = Model(); using var cancel = new CancellationTokenSource(); env.BeforeProbe = () => cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().PrepareAsync(model, cancel.Token));
        Assert.Equal("CPU", model.Compute); Assert.Empty(Directory.GetDirectories(Path.Combine(root, "runtime"), "probe-*"));
        env.BeforeProbe = null; Assert.Equal("CUDA", (await Service().PrepareAsync(model)).Models.Compute);
    }
    [Fact] public void SettingsAndQueuedSnapshotsCompareAndSwapButActiveJobsAreImmutable()
    {
        using var store = new LibraryStore(Path.Combine(root, "store")); var expected = store.Settings.Models; var prepared = expected with { Compute = "CUDA" };
        store.UpdateSettings(store.Settings with { Models = expected with { ComputePreference = "CPU" } });
        Assert.False(store.TryApplyPreparedSettingsModels(expected, prepared)); Assert.Equal("CPU", store.Settings.Models.ComputePreference);
        var audio = Path.Combine(root, "source.wav"); File.WriteAllBytes(audio, [1]); var job = store.Import(audio);
        Assert.True(store.TryApplyPreparedJobModels(job.Id, job.Models, prepared)); Assert.True(store.TryStart(job.Id));
        Assert.False(store.TryApplyPreparedJobModels(job.Id, prepared, expected)); Assert.Equal("CUDA", store.Get(job.Id).Models.Compute);
    }
    [Fact] public void ShippedCudaCatalogueMatchesOfficialRecordedPins()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime-catalogue.json")));
        var pins = doc.RootElement.GetProperty("cuda_runtimes").EnumerateArray().ToDictionary(x => x.GetProperty("engine").GetString()!);
        Assert.Equal(3, pins.Count);
        Assert.Equal("afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a", pins["whisper"].GetProperty("sha256").GetString());
        Assert.Equal("c32434587e5b79b06a70cc6379c773e6bb3ba1f0fc3bce392fab5ddbfd202538", pins["llama"].GetProperty("sha256").GetString());
        Assert.Equal("8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6", pins["cudart"].GetProperty("sha256").GetString());
    }
    public void Dispose() { http.Dispose(); Directory.Delete(root, true); }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
    sealed class FakeEnvironment : IRuntimeEnvironment
    {
        public bool SupportsCuda { get; set; } = true; public string? Nvidia = "NVIDIA fixture, driver"; public bool Llama = true, Whisper = true; public int Probes; public Action? BeforeProbe;
        public Task<string?> NvidiaAsync(CancellationToken ct) => Task.FromResult(Nvidia);
        public Task<NativeProbe> RunAsync(string executable, string[] arguments, CancellationToken ct)
        {
            Probes++; BeforeProbe?.Invoke(); ct.ThrowIfCancellationRequested();
            return Task.FromResult(Path.GetFileName(executable) == "llama-cli.exe" ? new NativeProbe(0, Llama ? "CUDA0: NVIDIA fixture (16302 MiB)" : "CPU only") : new NativeProbe(3, Whisper ? "ggml_cuda_init: found 1 CUDA devices\nload_backend: loaded CUDA backend" : "failed to load CUDA backend"));
        }
    }
}
