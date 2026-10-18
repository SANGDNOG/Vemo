using System.Text.Json;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class ReadingIntegrityIndependentTests
{
    [Fact]
    public void Millisecond_offsets_map_repeated_sentences_in_order_without_interpolating_gaps()
    {
        const string text = "반복 문장입니다.\n반복 문장입니다. 마지막 문장입니다.";
        const string json = "{\"transcription\":[{\"offsets\":{\"from\":0,\"to\":5460},\"text\":\"반복 문장입니다.\"},{\"offsets\":{\"from\":9000,\"to\":12340},\"text\":\"반복 문장입니다.\"},{\"offsets\":{\"from\":15010,\"to\":19250},\"text\":\"마지막 문장입니다.\"}]}";
        var segments = WhisperTranscript.Parse(json, text);
        Assert.Equal(5.46, segments[0].EndSeconds); Assert.Equal(15.01, segments[2].StartSeconds);
        var rows = TranscriptDisplay.Rows(new(text, segments));
        Assert.Equal(text, string.Concat(rows.Select(x => x.Text)));
        Assert.Equal(new double?[] { 0, 9, 15.01 }, rows.Select(x => x.StartSeconds));
        Assert.Equal(new double?[] { 5.46, 12.34, 19.25 }, rows.Select(x => x.EndSeconds));
    }

    [Theory]
    [InlineData(-1, 1, 2, 3)]
    [InlineData(2, 1, 3, 4)]
    [InlineData(2, 3, 1, 4)]
    [InlineData(0, 3, 2, 2.5)]
    [InlineData(double.NaN, 1, 2, 3)]
    [InlineData(0, double.PositiveInfinity, 2, 3)]
    public void Invalid_timing_leaves_all_raw_text_readable_without_seek_claims(double a, double b, double c, double d)
    {
        const string raw = "첫 문장. 둘째 문장.";
        var rows = TranscriptDisplay.Rows(new(raw, [new(a, b, "첫 문장."), new(c, d, "둘째 문장.")]));
        Assert.Equal(raw, string.Concat(rows.Select(x => x.Text)));
        Assert.All(rows, x => { Assert.Null(x.StartSeconds); Assert.Null(x.EndSeconds); });
    }

    [Fact]
    public void Missing_or_text_mismatched_sidecar_cannot_attach_old_timing_to_new_revision()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vemo-reading-independent-" + Guid.NewGuid()).Replace("/var/", "/private/var/");
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, TranscriptTimingFile.FileName);
        try
        {
            const string original = "Old exact transcript.";
            Assert.Empty(TranscriptTimingFile.Read(path, original));
            File.WriteAllText(path, TranscriptTimingFile.Serialize(original, [new(2.5, 5.46, original)]));
            Assert.Equal(2.5, Assert.Single(TranscriptTimingFile.Read(path, original)).StartSeconds);
            Assert.Empty(TranscriptTimingFile.Read(path, "New exact transcript."));
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"SchemaVersion\":2}");
            Assert.Empty(TranscriptTimingFile.Read(path, original));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("2026-10-18T00:05:00+14:00")]
    [InlineData("2026-10-18T23:55:00-12:00")]
    public void Title_uses_written_offset_day_once_and_separate_day_does_not_rewrite_timestamp(string timestamp)
    {
        var job = new RecordingJob { Id = Guid.NewGuid().ToString(), AudioSha256 = "fixture", Title = "회의 — 2025-01-01 — 2026-10-18", RecordedAt = timestamp };
        Assert.Equal("회의 — 2026-10-18", RecordingTitles.Display(job));
        Assert.Equal(timestamp, job.RecordedAt);
        Assert.Equal("회의 — 2026-10-19", RecordingTitles.Display(job with { RecordingDate = "2026-10-19" }));
        Assert.Equal(timestamp, job.RecordedAt);
        Assert.Equal("회의", RecordingTitles.Display(job with { RecordedAt = null }));
    }

    [Theory]
    [InlineData("whisper_print_progress_callback: progress = 104%", true, 99)]
    [InlineData("whisper_print_progress_callback: progress = 100%", true, 99)]
    [InlineData("whisper_print_progress_callback: progress = 0%", true, 0)]
    [InlineData("whisper_print_progress_callback: progress = -1%", false, 0)]
    [InlineData("notes generation 52%", false, 0)]
    [InlineData("model loaded 52%", false, 0)]
    public void Native_progress_is_bounded_and_unmeasured_stages_do_not_create_percent(string line, bool accepted, double expected)
    {
        Assert.Equal(accepted, WhisperProgress.TryPercent(line, out var percent));
        if (accepted) Assert.Equal(expected, percent);
    }

    [Fact]
    public async Task Runner_ignores_foreign_and_late_progress_keeps_jitter_monotonic_and_notes_unmeasured()
    {
        using var f = new StoreFixture(); var id = f.Store.Import(f.Audio, "Progress fixture").Id;
        var engine = new ReportingEngine(); var runner = new JobRunner(f.Store, engine);
        var observed = new List<ProcessingProgress>();
        runner.ProgressChanged += (_, _) => { if (runner.Progress is { } value) observed.Add(value); };
        Assert.True(await runner.RunNextAsync());
        Assert.Equal(JobState.Completed, f.Store.Get(id).State);
        var percentages = observed.Where(x => x.Stage == "transcribing" && x.Percent != null).Select(x => x.Percent!.Value).ToArray();
        Assert.NotEmpty(percentages); Assert.All(percentages, x => Assert.InRange(x, 0, 99));
        Assert.Equal(percentages.Order(), percentages); Assert.Equal(99, percentages[^1]);
        Assert.All(observed.Where(x => x.Stage == "notes"), x => Assert.Null(x.Percent));
        Assert.Contains(observed, x => x.Stage == "notes");
        Assert.All(observed, x => { Assert.Equal(id, x.JobId); Assert.True(x.Elapsed >= TimeSpan.Zero); });
        Assert.Equal(observed.Select(x => x.Elapsed).Order(), observed.Select(x => x.Elapsed));
        Assert.Equal("completed", runner.Progress!.Stage);
        var final = runner.Progress; engine.Context!.Progress!.Report(new(id, "transcribing", 17, TimeSpan.Zero));
        Assert.Equal(final, runner.Progress);
    }

    [Fact]
    public void Retry_keeps_committed_timing_pair_until_fresh_revision_commits_and_plain_ZIP_stays_plain()
    {
        using var f = new StoreFixture(); f.Store.UpdateSettings(new() { OutputDirectory = Path.Combine(f.Root, "export"), Models = new() { WhisperModel = "old-model" } });
        var id = f.Store.Import(f.Audio, "Revision fixture").Id;
        Assert.True(f.Store.TryStart(id)); f.Store.SaveTranscript(id, "Old transcript.", [new(2, 5.46, "Old transcript.")]); f.Store.Complete(id, "Old notes.");
        f.Store.Fail(id, "retry fixture");
        f.Store.UpdateSettings(f.Store.Settings with { Models = f.Store.Settings.Models with { WhisperModel = "new-model" } });
        f.Store.Retry([id], true); var pending = f.Store.Get(id).PendingArtifactRevision;
        Assert.NotNull(pending); Assert.True(f.Store.TryStart(id));
        f.Store.SaveTranscript(id, "New transcript.", [new(9, 12.34, "New transcript.")]);
        Assert.Equal("Old transcript.", f.Store.ReadTranscriptDocument(id).Text);
        Assert.Equal(2, Assert.Single(f.Store.ReadTranscriptDocument(id).Segments).StartSeconds);
        f.Store.Fail(id, "cancel fixture", true);
        f.Store.Retry([id]); Assert.Equal(pending, f.Store.Get(id).PendingArtifactRevision); Assert.True(f.Store.TryStart(id));
        f.Store.Complete(id, "New notes.");
        Assert.Equal(pending, f.Store.Get(id).ArtifactRevision); Assert.Null(f.Store.Get(id).PendingArtifactRevision);
        Assert.Equal("New transcript.", f.Store.ReadTranscriptDocument(id).Text);
        Assert.Equal(9, Assert.Single(f.Store.ReadTranscriptDocument(id).Segments).StartSeconds);
        using var zip = System.IO.Compression.ZipFile.OpenRead(f.Store.Export(id));
        Assert.Equal(new[] { "notes.md", "original.wav", "transcript.txt" }, zip.Entries.Select(x => x.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("transcript.txt")!.Open()); Assert.Equal("New transcript.", reader.ReadToEnd());
    }

    sealed class ReportingEngine : IInferenceEngine
    {
        public InferenceContext? Context;
        public Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct)
        {
            Context = context; var report = context.Progress!;
            report.Report(new("foreign-job", "transcribing", 98, TimeSpan.Zero));
            foreach (var percent in new[] { -1d, 12d, 7d, 12d, 58d, 104d, 77d, double.NaN }) report.Report(new(context.Job.Id, "transcribing", percent, TimeSpan.Zero));
            return Task.FromResult("Exact fixture transcript.");
        }
        public Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
        {
            context.Progress!.Report(new(context.Job.Id, "notes", 83, TimeSpan.Zero));
            context.Progress.Report(new(context.Job.Id, "transcribing", 19, TimeSpan.Zero));
            return Task.FromResult("{\"schema_version\":1,\"topics\":[{\"heading\":\"Fixture\",\"summary\":\"Fixture summary\",\"conditions\":[],\"examples\":[],\"refs\":[1]}]}");
        }
    }
    sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vemo-reading-store-independent-" + Guid.NewGuid()).Replace("/var/", "/private/var/");
        public string Audio => Path.Combine(Root, "source.wav");
        public LibraryStore Store { get; }
        public StoreFixture() { Directory.CreateDirectory(Root); File.WriteAllBytes(Audio, [82, 73, 70, 70, 1]); Store = new(Path.Combine(Root, "library")); Store.UpdateSettings(new() { AutoExport = false }); }
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }
}
