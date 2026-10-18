using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class RetranscriptionTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-retranscribe-" + Guid.NewGuid());
    string State => Path.Combine(root, "state");
    const string OldText = "반복 문장. 반복 문장.";
    const string FreshText = "새 원음 전사.";
    static readonly TranscriptSegment[] OldTiming = [new(0, 10, OldText)];
    static readonly TranscriptSegment[] FreshTiming = [new(20, 25, FreshText)];
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    RecordingJob Seed(LibraryStore store, bool revisioned = false)
    {
        Directory.CreateDirectory(root);
        var audio = Path.Combine(root, "source.wav"); File.WriteAllBytes(audio, [1, 2, 3]);
        store.UpdateSettings(store.Settings with { AutoExport = false, OutputDirectory = Path.Combine(root, "exports") });
        var date = "2026-10-18T00:00:00Z";
        var mobile = new MobileOrigin(Path.Combine(root, "sync"), Guid.NewGuid().ToString(), SafeFiles.Hash(audio), store.ProcessorId, date, "phone");
        var job = store.Import(audio, "phone", date, mobile);
        store.Rename(job.Id, "내 수동 제목");
        if (revisioned) { store.Fail(job.Id, "initial fixture"); store.Retry([job.Id]); }
        Assert.True(store.TryStart(job.Id)); store.SaveTranscript(job.Id, OldText, OldTiming); store.Complete(job.Id, "old notes");
        store.Export(job.Id);
        return store.Get(job.Id);
    }

    [Theory]
    [InlineData(JobState.Completed, false)]
    [InlineData(JobState.Failed, false)]
    [InlineData(JobState.Cancelled, false)]
    [InlineData(JobState.Completed, true)]
    [InlineData(JobState.Failed, true)]
    [InlineData(JobState.Cancelled, true)]
    public async Task RetranscribeCallsAsrAndReplacesPairOnlyAfterSuccess(JobState state, bool revisioned)
    {
        using var store = new LibraryStore(State); var committed = Seed(store, revisioned); var id = committed.Id;
        if (state != JobState.Completed) store.Fail(id, "fixture", state == JobState.Cancelled);
        store.UpdateSettings(store.Settings with { Models = store.Settings.Models with { WhisperProfile = "small" } });
        var exportBytes = File.ReadAllBytes(committed.ExportPath!);
        store.Retranscribe([id]);
        var queued = store.Get(id);
        Assert.Equal(JobState.Queued, queued.State); Assert.NotNull(queued.PendingArtifactRevision);
        Assert.Equal(committed.ArtifactRevision, queued.ArtifactRevision); Assert.Equal(committed.Models, queued.Models);
        Assert.Equal(committed.ExportPath, queued.ExportPath); Assert.Null(queued.Error);
        Assert.Equal("", store.ReadTranscriptForProcessing(id)); Assert.Empty(Directory.GetFiles(store.WorkDirectory(id)));
        AssertOldPair(store, id);
        var engine = new FreshEngine(store, id, committed);
        Assert.True(await new JobRunner(store, engine).RunNextAsync());
        Assert.Equal(1, engine.AsrCalls); Assert.Equal(1, engine.NotesCalls);
        var completed = store.Get(id);
        Assert.Equal(JobState.Completed, completed.State); Assert.Equal(queued.PendingArtifactRevision, completed.ArtifactRevision);
        Assert.Null(completed.PendingArtifactRevision); Assert.Null(completed.ExportPath);
        Assert.Equal(committed.Title, completed.Title); Assert.Equal(TitleOwnership.Manual, completed.TitleOwnership);
        Assert.Equal(committed.TitleRevision, completed.TitleRevision); Assert.Equal(committed.Mobile, completed.Mobile);
        Assert.Equal(committed.RecordedAt, completed.RecordedAt); Assert.Equal(committed.AudioSha256, completed.AudioSha256);
        Assert.Equal(FreshText, store.ReadTranscript(id)); Assert.Equal(FreshTiming, store.ReadTranscriptDocument(id).Segments);
        Assert.Contains(FreshText, store.ReadNotes(id)); Assert.DoesNotContain(OldText, store.ReadNotes(id));
        Assert.Equal(exportBytes, File.ReadAllBytes(committed.ExportPath!));
        // Committed files are retained even after publishing the replacement revision.
        var oldDirectory = Path.Combine(State, "items", id);
        if (committed.ArtifactRevision != null) oldDirectory = Path.Combine(oldDirectory, "artifacts", committed.ArtifactRevision);
        Assert.Equal(OldText, File.ReadAllText(Path.Combine(oldDirectory, "transcript.txt")));
        Assert.Equal("old notes", File.ReadAllText(Path.Combine(oldDirectory, "notes.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPendingIsNeverReusedAndFailedOrCancelledReplacementSurvivesReopen(bool cancel)
    {
        string id; string pending; RecordingJob committed;
        using (var store = new LibraryStore(State))
        {
            committed = Seed(store, revisioned: true); id = committed.Id;
            store.Fail(id, "notes failure"); store.Retry([id]);
            // Ordinary Retry clears the export pointer, but retains the exported file.
            Assert.Null(store.Get(id).ExportPath);
            var abandoned = store.Get(id).PendingArtifactRevision;
            store.SaveTranscript(id, "cached bad pending ASR", [new(1, 2, "cached bad pending ASR")]);
            store.Fail(id, "pending failed");
            store.Retranscribe([id]); pending = store.Get(id).PendingArtifactRevision!;
            Assert.NotEqual(abandoned, pending); Assert.NotNull(pending);
            Assert.Equal("", store.ReadTranscriptForProcessing(id)); Assert.Empty(Directory.GetFiles(store.WorkDirectory(id)));
            var engine = new FreshEngine(store, id, committed) { StopInNotes = true };
            using var cts = new CancellationTokenSource();
            if (cancel) engine.BeforeStop = cts.Cancel;
            Assert.True(await new JobRunner(store, engine).RunNextAsync(cts.Token));
            Assert.Equal(1, engine.AsrCalls);
            Assert.Equal(cancel ? JobState.Cancelled : JobState.Failed, store.Get(id).State);
            AssertOldPair(store, id);
        }
        using var reopened = new LibraryStore(State);
        Assert.Equal(pending, reopened.Get(id).PendingArtifactRevision); AssertOldPair(reopened, id);
        Assert.Null(reopened.Get(id).ExportPath); Assert.True(File.Exists(committed.ExportPath));
        Assert.Equal(FreshText, reopened.ReadTranscriptForProcessing(id));
        reopened.Retranscribe([id]);
        Assert.NotEqual(pending, reopened.Get(id).PendingArtifactRevision); Assert.Equal("", reopened.ReadTranscriptForProcessing(id));
        AssertOldPair(reopened, id);
    }

    [Theory]
    [InlineData(JobState.Queued, false)]
    [InlineData(JobState.Transcribing, false)]
    [InlineData(JobState.GeneratingNotes, false)]
    [InlineData(JobState.Completed, true)]
    [InlineData(JobState.Failed, true)]
    [InlineData(JobState.Cancelled, true)]
    [InlineData(JobState.Purged, true)]
    public void IneligibleJobsAreUnchanged(JobState state, bool trashed)
    {
        using var store = new LibraryStore(State); var job = Seed(store); var id = job.Id;
        if (state is JobState.Queued or JobState.Transcribing or JobState.GeneratingNotes)
        {
            store.Fail(id, "fixture"); store.Retry([id]);
            if (state != JobState.Queued) Assert.True(store.TryStart(id));
            if (state == JobState.GeneratingNotes) store.SaveTranscript(id, OldText, OldTiming);
        }
        if (state is JobState.Failed or JobState.Cancelled) store.Fail(id, "fixture", state == JobState.Cancelled);
        if (trashed) store.Trash([id]);
        if (state == JobState.Purged) store.Purge([id]);
        var before = store.Get(id); Assert.Equal(state, before.State);
        store.Retranscribe([id]); Assert.Equal(before, store.Get(id));
    }

    [Fact]
    public async Task OrdinaryRetryStillReusesCachedAsr()
    {
        using var store = new LibraryStore(State); var committed = Seed(store); var id = committed.Id;
        store.Fail(id, "notes failure"); store.Retry([id]);
        var pending = store.Get(id).PendingArtifactRevision;
        store.Fail(id, "notes failure again"); store.Retry([id]);
        Assert.Equal(pending, store.Get(id).PendingArtifactRevision);
        var engine = new CachedEngine(); Assert.True(await new JobRunner(store, engine).RunNextAsync());
        Assert.Equal(0, engine.AsrCalls); Assert.Equal(OldText, store.ReadTranscript(id));
        Assert.Equal(OldTiming, store.ReadTranscriptDocument(id).Segments);
    }

    static void AssertOldPair(LibraryStore store, string id)
    {
        Assert.Equal(OldText, store.ReadTranscript(id)); Assert.Equal("old notes", store.ReadNotes(id));
        Assert.Equal(OldTiming, store.ReadTranscriptDocument(id).Segments);
    }
    static string Notes(string quote) => new NotesDocument([new("원음 주제", "설명", [quote], [], [1])]).ToJson();
    sealed class FreshEngine(LibraryStore store, string id, RecordingJob committed) : IInferenceEngine
    {
        public int AsrCalls; public int NotesCalls; public bool StopInNotes; public Action? BeforeStop;
        public Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct)
        {
            AsrCalls++; AssertOldPair(store, id);
            Assert.Equal(store.AudioPath(id), context.AudioPath); Assert.Equal(committed.Models, context.Job.Models);
            Assert.Equal(committed.Mobile, context.Job.Mobile);
            Assert.Empty(Directory.GetFiles(context.WorkDirectory));
            SafeFiles.Atomic(Path.Combine(context.WorkDirectory, TranscriptTimingFile.FileName), TranscriptTimingFile.Serialize(FreshText, FreshTiming));
            return Task.FromResult(FreshText);
        }
        public Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
        {
            NotesCalls++; AssertOldPair(store, id); Assert.Equal(new[] { FreshText }, sources);
            if (StopInNotes) { BeforeStop?.Invoke(); ct.ThrowIfCancellationRequested(); throw new IOException("notes failed"); }
            return Task.FromResult(Notes(sources[0]));
        }
    }
    sealed class CachedEngine : IInferenceEngine
    {
        public int AsrCalls;
        public Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct) { AsrCalls++; throw new InvalidOperationException("Retry must reuse ASR."); }
        public Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
        { Assert.Equal(new[] { OldText }, sources); return Task.FromResult(Notes(sources[0])); }
    }
}
