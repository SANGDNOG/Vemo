using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Vemo.Core;

public sealed record InferenceContext(RecordingJob Job, string AudioPath, string WorkDirectory, IProgress<ProcessingProgress>? Progress = null)
{
    public string? NotesCheckpointDirectory { get; init; }
}
public interface IInferenceEngine
{
    Task<string> TranscribeAsync(InferenceContext context, CancellationToken cancellationToken);
    Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken cancellationToken);
}
public sealed record NoteTopic(string Heading, string Summary, string[] Conditions, string[] Examples, int[] Refs);
public sealed record NotesDocument(IReadOnlyList<NoteTopic> Topics)
{
    public static NotesDocument Parse(string text, int sourceCount)
    {
        using var doc = JsonFormat.Strict(text); var root = doc.RootElement;
        Keys(root, "schema_version", "topics"); if (root.GetProperty("schema_version").GetInt32() != 1) throw new InvalidDataException("Unsupported notes schema.");
        var topics = new List<NoteTopic>(); foreach (var t in root.GetProperty("topics").EnumerateArray())
        {
            Keys(t, "heading", "summary", "conditions", "examples", "refs");
            string Required(string key) { var s = t.GetProperty(key).GetString(); if (string.IsNullOrWhiteSpace(s)) throw new InvalidDataException("Empty note field."); return s; }
            string[] Strings(string key) => t.GetProperty(key).EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Null note text.")).ToArray();
            var refs = t.GetProperty("refs").EnumerateArray().Select(x => x.GetInt32()).ToArray(); if (refs.Length == 0 || refs.Any(r => r < 1 || r > sourceCount)) throw new InvalidDataException("Notes contain invalid source references.");
            topics.Add(new(Required("heading"), Required("summary"), Strings("conditions"), Strings("examples"), refs));
        }
        if (topics.Count == 0 || topics.Count > Math.Min(8192L, Math.Max(100L, (long)sourceCount * 32))) throw new InvalidDataException("Notes topic count exceeds the bounded source-dependent limit."); return new(topics);
    }
    public static NotesDocument Parse(string text, IReadOnlyList<string> sources)
    {
        var notes = Parse(text, sources.Count);
        static string WithoutWhitespace(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        var resolved = new List<NoteTopic>();
        foreach (var topic in notes.Topics)
        {
            // Reference order is metadata; source text always follows transcript order.
            // Deduplicate indices only, never repeated source content.
            var references = topic.Refs.Distinct().Order().ToArray();
            string SourceQuote(string quote)
            {
                var candidate = WithoutWhitespace(quote);
                if (candidate.Length == 0) throw new InvalidDataException("Empty notes quote.");
                // Stream a KMP match across maximal cited runs. Memory is O(quote
                // length + cited indices), independent of the total source text size.
                var prefix = new int[candidate.Length];
                for (int i = 1, length = 0; i < candidate.Length; i++)
                {
                    while (length > 0 && candidate[i] != candidate[length]) length = prefix[length - 1];
                    if (candidate[i] == candidate[length]) length++;
                    prefix[i] = length;
                }
                var positions = new (int Reference, int Offset)[candidate.Length];
                int matched = 0, next = 0, previous = 0;
                foreach (var reference in references)
                {
                    if (reference != previous + 1) { matched = 0; next = 0; }
                    previous = reference;
                    var source = sources[reference - 1];
                    for (int offset = 0; offset < source.Length; offset++)
                    {
                        var c = source[offset];
                        if (char.IsWhiteSpace(c)) continue;
                        positions[next] = (reference, offset);
                        next = (next + 1) % candidate.Length;
                        while (matched > 0 && c != candidate[matched]) matched = prefix[matched - 1];
                        if (c == candidate[matched]) matched++;
                        if (matched != candidate.Length) continue;

                        // The ring retains the exact endpoints of the matched span.
                        // Source boundaries contribute the transcript's newline separator.
                        var first = positions[next];
                        if (first.Reference == reference) return source[first.Offset..(offset + 1)];
                        var span = new StringBuilder(sources[first.Reference - 1][first.Offset..]);
                        for (int r = first.Reference + 1; r < reference; r++)
                            span.Append('\n').Append(sources[r - 1]);
                        return span.Append('\n').Append(source.AsSpan(0, offset + 1)).ToString();
                    }
                }
                throw new InvalidDataException("Notes conditions/examples must match a cited source or consecutive cited sources except for whitespace; numbers, negation and punctuation cannot be repaired.");
            }
            resolved.Add(topic with { Conditions = topic.Conditions.Select(SourceQuote).ToArray(), Examples = topic.Examples.Select(SourceQuote).ToArray() });
        }
        return new(resolved);
    }
    internal static void Keys(JsonElement obj, params string[] keys) { if (obj.ValueKind != JsonValueKind.Object || !obj.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(keys)) throw new InvalidDataException("Unexpected JSON fields."); }
    public string ToJson() => JsonSerializer.Serialize(new { schema_version = 1, topics = Topics.Select(topic => new { heading = topic.Heading, summary = topic.Summary, conditions = topic.Conditions, examples = topic.Examples, refs = topic.Refs }) });
    public string Markdown(string title)
    {
        var b = new StringBuilder().Append("# ").AppendLine(title).AppendLine();
        foreach (var t in Topics) { b.Append("## ").AppendLine(t.Heading).AppendLine().AppendLine(t.Summary).AppendLine(); if (t.Conditions.Length > 0) { b.AppendLine("### 조건 / 전제"); foreach (var c in t.Conditions) b.Append("- ").AppendLine(c); } if (t.Examples.Length > 0) { b.AppendLine("### 예시"); foreach (var e in t.Examples) b.Append("- ").AppendLine(e); } b.Append("출처: ").AppendLine(string.Join(", ", t.Refs.Select(x => $"[{x}]"))).AppendLine(); }
        return b.ToString();
    }
}
public sealed class JobRunner(LibraryStore store, IInferenceEngine engine, IRuntimePreparation? runtimePreparation = null)
{
    readonly SemaphoreSlim gate = new(1, 1); readonly object cancelGate = new(); CancellationTokenSource? current;
    string? activeJobId; ProcessingProgress? progress; Stopwatch? elapsed;
    public string? ActiveJobId { get { lock (cancelGate) return activeJobId; } }
    public ProcessingProgress? Progress { get { lock (cancelGate) return progress == null ? null : progress with { Elapsed = elapsed?.Elapsed ?? progress.Elapsed }; } }
    public event EventHandler? ProgressChanged;
    void NotifyProgress() { try { ProgressChanged?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* Observers cannot fail an inference job. */ } }
    void Stage(string jobId, string stage)
    {
        lock (cancelGate) progress = new(jobId, stage, null, elapsed?.Elapsed ?? TimeSpan.Zero);
        NotifyProgress();
    }
    sealed class ReportProgress(Action<ProcessingProgress> report) : IProgress<ProcessingProgress>
    { public void Report(ProcessingProgress value) => report(value); }
    public void CancelActive() { lock (cancelGate) current?.Cancel(); }
    public async Task<bool> RunNextAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return false;
        string? id = null; CancellationTokenSource? linked = null;
        try
        {
            if (store.Settings.QueuePaused) return false;
            var next = store.Jobs.FirstOrDefault(j => !j.Trashed && j.State == JobState.Queued); if (next == null) return false;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (cancelGate) { current = linked; activeJobId = next.Id; elapsed = Stopwatch.StartNew(); }
            Stage(next.Id, "preparing");
            var ct = linked.Token;
            if (runtimePreparation != null)
            {
                var prepared = await runtimePreparation.PrepareAsync(next.Models, ct);
                ct.ThrowIfCancellationRequested();
                if (!prepared.Ready || !store.TryApplyPreparedJobModels(next.Id, next.Models, prepared.Models)) return false;
                next = store.Get(next.Id);
            }
            ct.ThrowIfCancellationRequested();
            if (!store.TryStart(next.Id)) return false;
            id = next.Id;
            var reporter = new ReportProgress(value =>
            {
                lock (cancelGate)
                {
                    if (activeJobId != id || current != linked || progress?.Stage is not ("transcribing" or "notes") || value.JobId != id || value.Stage != progress.Stage || value.Percent is not double percent || !double.IsFinite(percent)) return;
                    progress = new(id, progress.Stage, Math.Max(progress.Percent ?? 0, Math.Clamp(percent, 0, progress.Stage == "notes" ? 100 : 99)), elapsed?.Elapsed ?? TimeSpan.Zero);
                }
                NotifyProgress();
            });
            var audioPath = store.AudioPath(id);
            var context = new InferenceContext(store.Get(id), audioPath, store.WorkDirectory(id), reporter)
            { NotesCheckpointDirectory = Path.Combine(Path.GetDirectoryName(audioPath)!, "notes-checkpoints") };
            if (SafeFiles.Hash(context.AudioPath) != context.Job.AudioSha256) throw new IOException("Owned audio hash mismatch.");
            var transcript = store.ReadTranscriptForProcessing(id);
            IReadOnlyList<TranscriptSegment> timings;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Stage(id, "transcribing");
                var timingPath = Path.Combine(context.WorkDirectory, TranscriptTimingFile.FileName); SafeFiles.NoLinks(timingPath); File.Delete(timingPath);
                transcript = await engine.TranscribeAsync(context, ct); if (string.IsNullOrWhiteSpace(transcript)) throw new InvalidDataException("Transcriber returned empty text.");
                timings = TranscriptTimingFile.Read(timingPath, transcript);
            }
            else timings = store.ReadProcessingTimings(id, transcript);
            ct.ThrowIfCancellationRequested(); store.SaveTranscript(id, transcript, timings); Stage(id, "notes");
            var sources = SourceLines(transcript); var json = await engine.GenerateNotesAsync(context, sources, ct); ct.ThrowIfCancellationRequested(); var notes = NotesDocument.Parse(json, sources);
            Stage(id, "saving"); SafeFiles.Atomic(Path.Combine(context.WorkDirectory, "notes.json"), notes.ToJson()); store.Complete(id, notes, context.Job);
            if (store.Settings.AutoExport) { try { store.Export(id); } catch (Exception e) { store.SetExportError(id, e.Message); } }
            Stage(id, "completed"); return true;
        }
        catch (OperationCanceledException) { if (id == null) throw; store.Fail(id, "Processing cancelled; existing transcript is preserved.", true); Stage(id, "cancelled"); return true; }
        catch (Exception e) { if (id == null) throw; store.Fail(id, e.Message); Stage(id, "failed"); return true; }
        finally { lock (cancelGate) { current = null; linked?.Dispose(); activeJobId = null; elapsed?.Stop(); if (progress != null) progress = progress with { Elapsed = elapsed?.Elapsed ?? progress.Elapsed }; elapsed = null; } gate.Release(); }
    }
    public static IReadOnlyList<string> SourceLines(string text)
    {
        var lines = new List<string>(); foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) for (int p = 0; p < line.Length; p += 500) lines.Add(line.Substring(p, Math.Min(500, line.Length - p)));
        if (lines.Count == 0) throw new InvalidDataException("Transcript has no sources."); return lines;
    }
}
public sealed class LocalCliInferenceEngine : IInferenceEngine
{
    static void Required(string path, string label, bool executable = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException($"Choose an existing local {label} file in Settings; nothing is downloaded automatically.");
        SafeFiles.NoLinks(path);
        if (executable && OperatingSystem.IsWindows() && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{label} must be a native .exe, not a script.");
    }
    static void Runtime(ModelSelection m) { if (m.Compute == "CUDA" && !m.CudaConfirmed) throw new InvalidOperationException("CUDA requires an explicitly confirmed compatible runtime and NVIDIA device; choose CPU otherwise."); }
    public async Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct)
    {
        var m = context.Job.Models; Runtime(m); Required(m.WhisperExecutable, "whisper.cpp executable", true); Required(m.WhisperModel, "Whisper model");
        var input = context.AudioPath;
        // Decode all source formats to the exact PCM format whisper.cpp expects.
        Required(m.FfmpegExecutable, "FFmpeg executable (audio decoding)", true);
        input = Path.Combine(context.WorkDirectory, "input.wav"); SafeFiles.NoLinks(input);
        await ProcessRunner.RunAsync(m.FfmpegExecutable, ["-nostdin", "-y", "-i", context.AudioPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", input], context.WorkDirectory, m.TimeoutMinutes, ct);
        var output = Path.Combine(context.WorkDirectory, "asr");
        foreach (var path in new[] { output + ".txt", output + ".json", Path.Combine(context.WorkDirectory, TranscriptTimingFile.FileName) }) { SafeFiles.NoLinks(path); File.Delete(path); }
        var args = new List<string> { "-m", m.WhisperModel, "-f", input, "-l", "ko", "-otxt", "-oj", "-pp", "-of", output }; if (m.Compute == "CPU") args.Add("-ng");
        await ProcessRunner.RunAsync(m.WhisperExecutable, args, context.WorkDirectory, m.TimeoutMinutes, ct, line =>
        {
            if (WhisperProgress.TryPercent(line, out var percent)) context.Progress?.Report(new(context.Job.Id, "transcribing", percent, TimeSpan.Zero));
        });
        if (!File.Exists(output + ".txt")) throw new IOException("whisper.cpp did not produce its text output.");
        var text = SafeFiles.Read(output + ".txt").Trim();
        // Optional alignment never destroys valid text. Unavailable/mismatched JSON leaves it untimed.
        if (File.Exists(output + ".json"))
        {
            try
            {
                var segments = WhisperTranscript.Parse(SafeFiles.Read(output + ".json"), text);
                SafeFiles.Atomic(Path.Combine(context.WorkDirectory, TranscriptTimingFile.FileName), TranscriptTimingFile.Serialize(text, segments));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException) { }
        }
        ct.ThrowIfCancellationRequested(); return text;
    }
    public async Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
    {
        var m = context.Job.Models; Runtime(m); Required(m.LlamaExecutable, "llama.cpp llama-cli executable", true); Required(m.LlamaModel, "GGUF notes model");
        string modelIdentity;
        await using (var model = File.OpenRead(m.LlamaModel))
            modelIdentity = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(model, ct));
        return await LongNotes.GenerateAsync(context, sources, modelIdentity, async (prompt, sourceCount, token) =>
        {
            var file = Path.Combine(context.WorkDirectory, "prompt.txt"); SafeFiles.Atomic(file, prompt);
            var grammarFile = Path.Combine(context.WorkDirectory, "notes.gbnf"); SafeFiles.Atomic(grammarFile, NotesGrammar.Create(sourceCount));
            var responseFile = Path.Combine(context.WorkDirectory, "llama-response-" + Guid.NewGuid() + ".txt");
            SafeFiles.NoLinks(responseFile);
            var args = new List<string> {"-m", m.LlamaModel, "-f", file, "--output-file", responseFile, "--single-turn", "--jinja", "--reasoning", "off", "--chat-template-kwargs", "{\"enable_thinking\":false}", "--offline", "--no-display-prompt", "--no-show-timings", "--color", "off", "--simple-io", "-c", "16384", "-n", "6000", "--temp", "0.2", "-ngl", m.Compute == "CPU" ? "0" : m.AutoGpuLayers ? "auto" : m.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (m.Compute != "CPU" && m.AutoGpuLayers) args.AddRange(["--fit", "on", "--fit-target", "2048"]);
            args.AddRange(["--grammar-file", grammarFile]);
            await ProcessRunner.RunAsync(m.LlamaExecutable, args, context.WorkDirectory, m.TimeoutMinutes, token);
            if (!File.Exists(responseFile)) throw new IOException("llama-cli did not produce the required b11714 output-file session; stdout banners are not accepted as notes.");
            var json = LlamaOutput.ParseSession(SafeFiles.Read(responseFile), prompt, constrainedNotes: true);
            return json;
        }, ct);
    }
}
// b11714 tools/cli/cli-context.cpp:623,640–652 writes an exact prompt/session wrapper.
// This intentionally does not scan stdout or search for a JSON-looking substring.
public static class LlamaOutput
{
    public static string ParseSession(string session, string prompt) => ParseSession(session, prompt, constrainedNotes: false);

    public static string ParseSession(string session, string prompt, bool constrainedNotes)
    {
        static string Newlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
        session = Newlines(session); prompt = Newlines(prompt);
        // The CLI removes exactly one final newline from its submitted prompt.
        if (prompt.EndsWith('\n')) prompt = prompt[..^1];
        var prefix = "User:\n" + prompt + "\n\nAssistant:\n";
        if (!session.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected llama-cli output-file wrapper. This adapter requires the pinned b11714 single-turn session format and exact prompt identity.");
        var payload = session[prefix.Length..];
        if (constrainedNotes)
        {
            if (!payload.StartsWith(NotesGrammar.AssistantPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected constrained notes generation prefix.");
            payload = payload[NotesGrammar.AssistantPrefix.Length..];
        }
        payload = payload.Trim();
        using var doc = JsonFormat.Strict(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Llama assistant payload must be exactly one JSON object.");
        return payload;
    }
}
public static class ProcessRunner
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, int timeoutMinutes, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        SafeFiles.NoLinks(executable); SafeFiles.NoLinks(workingDirectory); ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new() { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            if (!process.Start()) throw new IOException("Could not start inference runtime."); process.StandardInput.Close();
            using var registration = deadline.Token.Register(() => Kill(process));
            var stdout = ReadBounded(process.StandardOutput, process, deadline.Token, onOutputLine); var stderr = ReadBounded(process.StandardError, process, deadline.Token, onOutputLine);
            await process.WaitForExitAsync(deadline.Token); await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited {process.ExitCode}: " + (stderr.Result.Length > 2000 ? stderr.Result[^2000..] : stderr.Result));
            return stdout.Result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"Runtime exceeded {timeoutMinutes} minutes; process tree terminated."); }
        finally { Kill(process); }
    }
    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    static async Task<string> ReadBounded(StreamReader reader, Process p, CancellationToken ct, Action<string>? onLine)
    {
        var b = new StringBuilder(); var line = new StringBuilder(); var buf = new char[4096]; int n;
        while ((n = await reader.ReadAsync(buf, ct)) > 0)
        {
            if (b.Length + n > 4 * 1024 * 1024) { Kill(p); throw new IOException("Runtime output exceeded 4 MiB; process tree terminated."); }
            b.Append(buf, 0, n);
            if (onLine != null)
            {
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] is '\r' or '\n') { if (line.Length > 0) { onLine(line.ToString()); line.Clear(); } }
                    else if (line.Length < 8192) line.Append(buf[i]);
                }
            }
        }
        if (line.Length > 0) onLine?.Invoke(line.ToString()); return b.ToString();
    }
}
