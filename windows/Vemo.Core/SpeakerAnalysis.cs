using System.Globalization;
using System.Text.Json;
namespace Vemo.Core;

public interface ISpeakerAnalysisEngine
{
    Task<IReadOnlyList<SpeakerTurn>> AnalyzeAsync(InferenceContext context, CancellationToken ct);
}
public sealed partial class JobRunner
{
    public async Task AnalyzeSpeakersAsync(string id, ISpeakerAnalysisEngine speakerEngine, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("전사·노트·화자 분석이 끝난 뒤 다시 시도하세요.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); string? work = null;
        try
        {
            var job = store.Get(id);
            if (job.Trashed || job.State is JobState.Queued or JobState.Transcribing or JobState.GeneratingNotes or JobState.Purged) throw new InvalidOperationException("완료된 녹음을 선택하세요.");
            var transcript = store.ReadTranscriptDocument(id); TranscriptDisplay.Validate(transcript.Text, transcript.Segments);
            if (transcript.Segments.Any(s => s.EndSeconds > 3600)) throw new InvalidDataException("화자 분석은 최대 60분까지 지원합니다.");
            var previous = store.ReadSpeakerDocument(id)?.Serialize() ?? "";
            lock (cancelGate) { current = linked; activeJobId = id; elapsed = System.Diagnostics.Stopwatch.StartNew(); }
            Stage(id, "speakers");
            work = Path.Combine(store.WorkDirectory(id), "speakers-" + Guid.NewGuid()); Directory.CreateDirectory(work);
            var audio = store.AudioPath(id); if (SafeFiles.Hash(audio) != job.AudioSha256) throw new InvalidDataException("Owned audio changed.");
            var reporter = new ReportProgress(p =>
            {
                lock (cancelGate) { if (current != linked || p.Percent is not double percent || !double.IsFinite(percent)) return; progress = new(id, "speakers", Math.Max(progress?.Percent ?? 0, Math.Clamp(percent, 0, 100)), elapsed?.Elapsed ?? TimeSpan.Zero); }
                NotifyProgress();
            });
            var turns = await speakerEngine.AnalyzeAsync(new(job, audio, work, reporter), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (turns.Select(t => t.SpeakerId).Distinct().Take(21).Count() > 20) throw new InvalidDataException("화자가 20명을 초과했습니다. 화자 수를 지정해 다시 분석하세요.");
            var result = SpeakerDocument.Create(job.AudioSha256, transcript, turns);
            if (store.Get(id) != job || SafeFiles.Hash(audio) != job.AudioSha256) throw new InvalidDataException("Recording changed during analysis.");
            linked.Token.ThrowIfCancellationRequested();
            store.SaveSpeakerDocument(id, result, expectedDocument: previous, expectedJob: job, ct: linked.Token); Stage(id, "completed");
        }
        catch (OperationCanceledException) { Stage(id, "cancelled"); throw; }
        catch { Stage(id, "failed"); throw; }
        finally
        {
            if (work != null) { try { SafeFiles.DeleteTree(work); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            lock (cancelGate) { current = null; activeJobId = null; elapsed?.Stop(); elapsed = null; }
            gate.Release();
        }
    }
}
public sealed class LocalSpeakerAnalysisEngine(string helper, string models, int? speakerCount = null) : ISpeakerAnalysisEngine
{
    public async Task<IReadOnlyList<SpeakerTurn>> AnalyzeAsync(InferenceContext context, CancellationToken ct)
    {
        if (speakerCount is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(speakerCount), "화자 수는 1–20명이어야 합니다.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows helper required.");
        SafeFiles.NoLinks(helper); if (!File.Exists(helper)) throw new FileNotFoundException("화자 분석 도우미가 없습니다. Windows 배포의 diarization 폴더를 설치하세요.");
        SpeakerModels.Verify(models);
        var input = Path.Combine(context.WorkDirectory, "input.wav");
        // Read the whole source, with a hard decoded output size cap. Do not truncate
        // at one hour and accidentally publish an analysis of only part of a recording.
        await ProcessRunner.RunAsync(context.Job.Models.FfmpegExecutable,
            ["-nostdin", "-y", "-i", context.AudioPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-fs", "115204096", input], context.WorkDirectory, 30, ct);
        if (new FileInfo(input).Length >= 115204096) throw new InvalidDataException("화자 분석은 최대 60분까지 지원합니다.");
        var output = Path.Combine(context.WorkDirectory, "turns.json");
        var arguments = new List<string> { input, models, output };
        if (speakerCount is { } count) arguments.AddRange(["--num-clusters", count.ToString(CultureInfo.InvariantCulture)]);
        await ProcessRunner.RunAsync(helper, arguments, context.WorkDirectory, context.Job.Models.TimeoutMinutes, ct, line =>
        {
            if (line.StartsWith("VEMO_PROGRESS ", StringComparison.Ordinal) && double.TryParse(line[14..], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent)) context.Progress?.Report(new(context.Job.Id, "speakers", percent, TimeSpan.Zero));
        });
        using var json = JsonFormat.Strict(SafeFiles.Read(output));
        var turns = json.Deserialize<SpeakerTurn[]>() ?? throw new InvalidDataException("Missing speaker turns.");
        if (turns.Length > 100000) throw new InvalidDataException("Too many speaker turns."); return turns;
    }
}
