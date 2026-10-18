using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;
using static Vemo.App.Tests.DragDropTestInput;

namespace Vemo.App.Tests;

public sealed class NativeInferenceDropTests(ITestOutputHelper output)
{
    public static bool HasNativeInputs => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VEMO_TEST_AUDIO"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VEMO_TEST_SETTINGS"));

    [AvaloniaFact(Timeout = 300000, SkipUnless = nameof(HasNativeInputs),
        Skip = "Opt-in real inference requires both VEMO_TEST_AUDIO and VEMO_TEST_SETTINGS.")]
    public async Task Dropped_audio_runs_real_background_engine_and_exports_nested_folder_zip()
    {
        var audio = Path.GetFullPath(Environment.GetEnvironmentVariable("VEMO_TEST_AUDIO")!);
        var presetPath = Path.GetFullPath(Environment.GetEnvironmentVariable("VEMO_TEST_SETTINGS")!);
        Assert.True(File.Exists(audio), "Opt-in audio path must exist; no synthetic success if missing.");
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(presetPath), JsonFormat.Options)!;
        var originalHash = SafeFiles.Hash(audio);
        var title = Path.GetFileNameWithoutExtension(audio);
        string folderId = "", exportRoot = "";
        await using var ui = new UiFixture(seedRecordings: false, configure: store =>
        {
            exportRoot = Path.Combine(Path.GetDirectoryName(store.Root)!, "exports");
            store.UpdateSettings(settings with { QueuePaused = true, MobileDirectory = "", OutputDirectory = exportRoot });
            var parent = store.CreateFolder("검증", null);
            folderId = store.CreateFolder("음성", parent.Id).Id;
        });
        var timer = Stopwatch.StartNew();
        var liveRunner = (JobRunner)typeof(Vemo.App.MainWindow).GetField("runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ui.Window)!;
        var observed = new System.Collections.Concurrent.ConcurrentQueue<ProcessingProgress>();
        liveRunner.ProgressChanged += (_, _) => { if (liveRunner.Progress is { } progress) observed.Enqueue(progress); };
        using var data = await Files(ui.Window, audio);
        RawDrop(ui.Window, Folder(ui, "음성"), data);
        await Until(() => ui.Store.Jobs.Count == 1, "Actual Drop must import exactly one real input before enabling the queue.");
        var job = Assert.Single(ui.Store.Jobs);
        Assert.Equal(folderId, job.FolderId);
        Assert.Equal(originalHash, job.AudioSha256);
        Assert.Equal(title, job.Title);
        ui.Store.SetRecordedAt(job.Id, "2026-10-18T10:00:00+09:00");
        // This is the actual window's existing background JobRunner/LocalCliInferenceEngine.
        // Never call Import, RunNextAsync, SaveTranscript, Complete or Export in this test.
        ui.Store.UpdateSettings(ui.Store.Settings with { QueuePaused = false });
        while (timer.Elapsed < TimeSpan.FromSeconds(270))
        {
            Pump();
            job = ui.Store.Get(job.Id);
            if (job.State is JobState.Failed or JobState.Cancelled || job.State == JobState.Completed && job.ExportPath != null) break;
            await Task.Delay(100);
        }
        job = ui.Store.Get(job.Id);
        output.WriteLine($"Actual background pipeline: state={job.State}; elapsed={timer.Elapsed.TotalSeconds:F2}s; error={job.Error}; exportError={job.ExportError}");
        Assert.True(job.State == JobState.Completed, $"Actual pipeline ended at {job.State}: {job.Error}");
        Assert.Null(job.ExportError);
        Assert.Contains(observed, p => p.Stage == "transcribing" && p.Percent > 0);
        Assert.Contains(observed, p => p.Stage == "notes" && p.Percent == null);
        Assert.All(observed.Where(p => p.Stage == "transcribing" && p.Percent != null), p => Assert.InRange(p.Percent!.Value, 0, 99));
        output.WriteLine("Native progress stages: " + string.Join(", ", observed.Select(p => p.Stage + (p.Percent is { } value ? $" {value}%" : "")).Distinct()));
        Assert.False(string.IsNullOrWhiteSpace(ui.Store.ReadTranscript(job.Id)));
        Assert.False(string.IsNullOrWhiteSpace(ui.Store.ReadNotes(job.Id)));
        var document = ui.Store.ReadTranscriptDocument(job.Id);
        Assert.Equal(ui.Store.ReadTranscript(job.Id), document.Text);
        Assert.NotEmpty(document.Segments);
        TranscriptDisplay.Validate(document.Text, document.Segments);
        var rows = TranscriptDisplay.Rows(document);
        Assert.NotEmpty(rows);
        Assert.All(rows.Where(r => !string.IsNullOrWhiteSpace(r.Text)), r => Assert.NotNull(r.StartSeconds));
        Assert.Equal(document.Text, string.Concat(rows.Select(r => r.Text)));
        Assert.Equal(title + " — 2026-10-18", RecordingTitles.Display(job));
        Assert.NotNull(job.ExportPath);
        Assert.Equal(Path.Combine(exportRoot, "검증", "음성"), Path.GetDirectoryName(job.ExportPath));
        Assert.Equal(title + " - 2026-10-18.zip", Path.GetFileName(job.ExportPath));
        using (var zip = ZipFile.OpenRead(job.ExportPath))
        {
            var originalName = "original" + Path.GetExtension(audio).ToLowerInvariant();
            Assert.Equal(new[] { "notes.md", originalName, "transcript.txt" }.Order(), zip.Entries.Select(entry => entry.FullName).Order());
            using var original = zip.GetEntry(originalName)!.Open();
            Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant());
            using var transcript = new StreamReader(zip.GetEntry("transcript.txt")!.Open());
            using var notes = new StreamReader(zip.GetEntry("notes.md")!.Open());
            Assert.Equal(ui.Store.ReadTranscript(job.Id), transcript.ReadToEnd());
            Assert.Equal(ui.Store.ReadNotes(job.Id), notes.ReadToEnd());
        }
        Assert.Equal(originalHash, SafeFiles.Hash(audio));
        Assert.Equal(originalHash, SafeFiles.Hash(ui.Store.AudioPath(job.Id)));
        var root = ui.LibraryRoot;
        await ui.CloseAsync();
        using var reopened = new LibraryStore(root);
        Assert.Equal(JobState.Completed, reopened.Get(job.Id).State);
        Assert.Equal(job.ExportPath, reopened.Get(job.Id).ExportPath);
        Assert.Equal(document.Segments, reopened.ReadTranscriptDocument(job.Id).Segments);
        Assert.Equal(title + " — 2026-10-18", RecordingTitles.Display(reopened.Get(job.Id)));
        output.WriteLine($"Verified {document.Segments.Count} real timestamp segments, {rows.Count} readable rows and unchanged raw transcript after reopen.");
        output.WriteLine($"Verified unchanged input hash {originalHash}; three ZIP entries; nested logical folder; title/date filename; completed library survives reopen.");
    }
}
