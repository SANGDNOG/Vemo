using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class IntegrityTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-integrity-" + Guid.NewGuid());
    string P(params string[] parts) => Path.Combine([root, .. parts]);
    string Item(string id, string file) => P("state", "items", id, file);
    public IntegrityTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    LibraryStore Open() => new(P("state"));
    string Audio() { var p = P("source.wav"); File.WriteAllBytes(p, [1, 2, 3]); return p; }
    static void Done(LibraryStore store, string id) { Assert.True(store.TryStart(id)); store.SaveTranscript(id, "old transcript"); store.Complete(id, "old notes"); }
    string Request(LibraryStore store, string recording, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var dir = P("sync", "recordings", recording, hash); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "audio.m4a"), bytes);
        File.WriteAllText(Path.Combine(dir, "recording.json"), JsonSerializer.Serialize(new { schema_version = 1, recording_id = recording, audio_sha256 = hash, audio_bytes = bytes.Length, audio_file = "audio.m4a", recorded_at = "2026-10-18T01:02:03+09:00", title = "fixture", mode = "desktop", target_processor_id = store.ProcessorId, completed = true }));
        return dir;
    }
    [Theory]
    [InlineData("transcript.txt")]
    [InlineData("notes.md")]
    [InlineData("original.wav")]
    public void ExportRejectsEveryLinkedSourceWithoutCreatingAnArchive(string file)
    {
        using var store = Open(); store.UpdateSettings(new() { OutputDirectory = P("exports") }); var job = store.Import(Audio()); Done(store, job.Id);
        var canary = P("external-canary"); File.WriteAllText(canary, "EXTERNAL-CANARY"); var artifact = Item(job.Id, file); File.Delete(artifact); File.CreateSymbolicLink(artifact, canary);
        Assert.Throws<IOException>(() => store.Export(job.Id));
        Assert.Empty(Directory.EnumerateFiles(P("exports"))); Assert.Equal("EXTERNAL-CANARY", File.ReadAllText(canary));
    }
    [Fact]
    public void AudioPathRejectsLinkedFinalComponent()
    {
        using var store = Open(); var source = Audio(); var job = store.Import(source); var audio = Item(job.Id, "original.wav"); File.Delete(audio); File.CreateSymbolicLink(audio, source);
        Assert.Throws<IOException>(() => store.AudioPath(job.Id));
    }
    [Fact]
    public void WorkDirectoryRejectsLinkedFinalComponent()
    {
        using var store = Open(); var job = store.Import(Audio()); var outside = P("outside"); Directory.CreateDirectory(outside); Directory.CreateSymbolicLink(Item(job.Id, "work"), outside);
        Assert.Throws<IOException>(() => store.WorkDirectory(job.Id)); Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingUuidCannotBeImportedAgainWithDifferentBytes(bool purge)
    {
        string id; var recording = Guid.NewGuid().ToString();
        using (var store = Open())
        {
            var adapter = new MobileFolderAdapter(store); adapter.Register(P("sync")); Request(store, recording, [1, 2, 3]); Assert.Equal(1, (await adapter.ScanAsync()).Imported); id = Assert.Single(store.Jobs).Id;
            if (purge) { store.CancelQueued([id]); store.Trash([id]); store.Purge([id]); }
            Request(store, recording, [4, 5, 6]);
        }
        using var reopened = Open(); var scan = await new MobileFolderAdapter(reopened).ScanAsync();
        Assert.Equal(0, scan.Imported); Assert.Single(scan.Issues); Assert.Equal(id, Assert.Single(reopened.Jobs).Id); Assert.Equal(purge ? JobState.Purged : JobState.Queued, reopened.Get(id).State);
    }
    [Theory]
    [InlineData("notes.md", null)]
    [InlineData("notes.md", "")]
    [InlineData("notes.md", " \n\t")]
    [InlineData("transcript.txt", null)]
    [InlineData("transcript.txt", " \n")]
    public async Task PublicationRequiresBothCurrentNonemptyArtifacts(string file, string? replacement)
    {
        using var store = Open(); var adapter = new MobileFolderAdapter(store); adapter.Register(P("sync")); var recording = Guid.NewGuid().ToString(); Request(store, recording, [1, 2, 3]); await adapter.ScanAsync(); var job = Assert.Single(store.Jobs); Done(store, job.Id);
        if (replacement == null) File.Delete(Item(job.Id, file)); else File.WriteAllText(Item(job.Id, file), replacement);
        Assert.Throws<InvalidDataException>(() => adapter.Publish(job.Id)); Assert.False(Directory.Exists(P("sync", "results")));
    }
    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("missing")]
    [InlineData("link")]
    public async Task PublicationRevalidatesRemoteAudioBeforeWriting(string change)
    {
        using var store = Open(); var adapter = new MobileFolderAdapter(store); adapter.Register(P("sync")); var request = Request(store, Guid.NewGuid().ToString(), [1, 2, 3]); await adapter.ScanAsync(); var job = Assert.Single(store.Jobs); Done(store, job.Id);
        var audio = Path.Combine(request, "audio.m4a");
        if (change == "hash") File.WriteAllBytes(audio, [9, 9, 9]);
        else if (change == "size") File.WriteAllBytes(audio, [1, 2]);
        else { File.Delete(audio); if (change == "link") File.CreateSymbolicLink(audio, store.AudioPath(job.Id)); }
        var error = Record.Exception(() => adapter.Publish(job.Id)); Assert.True(error is IOException or InvalidDataException, error?.ToString() ?? "Publication unexpectedly succeeded."); Assert.False(Directory.Exists(P("sync", "results"))); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.AudioPath(job.Id)));
    }
    [Fact]
    public async Task InterruptedPurgePersistsSuppressionBeforeDeletionAndResumesAfterRestart()
    {
        string id; var recording = Guid.NewGuid().ToString(); var canary = P("external"); File.WriteAllText(canary, "keep");
        using (var store = Open())
        {
            var adapter = new MobileFolderAdapter(store); adapter.Register(P("sync")); Request(store, recording, [1, 2, 3]); await adapter.ScanAsync(); id = Assert.Single(store.Jobs).Id; store.CancelQueued([id]); store.Trash([id]);
            File.CreateSymbolicLink(Item(id, "stop-deletion"), canary);
            Assert.Throws<IOException>(() => store.Purge([id]));
            using var persisted = JsonDocument.Parse(File.ReadAllText(P("state", "library.json")));
            Assert.Equal("Purged", persisted.RootElement.GetProperty("Jobs")[0].GetProperty("State").GetString());
            Assert.Equal("keep", File.ReadAllText(canary)); Request(store, recording, [4, 5, 6]);
        }
        using (var obstructed = Open()) { Assert.Equal(JobState.Purged, obstructed.Get(id).State); Assert.Equal(0, (await new MobileFolderAdapter(obstructed).ScanAsync()).Imported); }
        File.Delete(Item(id, "stop-deletion"));
        using var recovered = Open(); Assert.Equal(JobState.Purged, recovered.Get(id).State); Assert.False(Directory.Exists(P("state", "items", id))); Assert.Equal("keep", File.ReadAllText(canary));
    }
    [Fact]
    public void PurgeDoesNotDeleteOwnedBytesIfIntentCannotBeSaved()
    {
        using var store = Open(); var job = store.Import(Audio()); store.CancelQueued([job.Id]); store.Trash([job.Id]);
        File.Delete(P("state", "library.json")); Directory.CreateDirectory(P("state", "library.json"));
        Assert.ThrowsAny<IOException>(() => store.Purge([job.Id])); Assert.True(File.Exists(Item(job.Id, "original.wav"))); Assert.Equal(JobState.Cancelled, store.Get(job.Id).State);
    }
    [Fact]
    public async Task ChangedAsrRetryKeepsCommittedOutputsThroughCancellationThenCommitsFreshPair()
    {
        string id;
        using (var store = Open())
        {
            store.UpdateSettings(new() { Models = new() { WhisperModel = "old.bin" } }); var job = store.Import(Audio()); id = job.Id; Done(store, id); store.Fail(id, "retry fixture");
            File.WriteAllText(Path.Combine(store.WorkDirectory(id), "old-work"), "preserve");
            store.UpdateSettings(new() { Models = new() { WhisperModel = "new.bin" } }); store.Retry([id], true);
            Assert.Equal("old transcript", store.ReadTranscript(id)); Assert.Equal("old notes", store.ReadNotes(id)); Assert.Equal("preserve", File.ReadAllText(Item(id, Path.Combine("work", "old-work"))));
            var engine = new FixtureEngine(wait: true); var runner = new JobRunner(store, engine); var task = runner.RunNextAsync(); await engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); runner.CancelActive(); await task;
            Assert.Equal(JobState.Cancelled, store.Get(id).State); Assert.Equal("old transcript", store.ReadTranscript(id)); Assert.Equal("old notes", store.ReadNotes(id)); Assert.Equal("new transcript", engine.Sources);
        }
        using var reopened = Open(); reopened.Retry([id]); var finishing = new FixtureEngine(); await new JobRunner(reopened, finishing).RunNextAsync();
        Assert.Equal(JobState.Completed, reopened.Get(id).State); Assert.Equal("new transcript", finishing.Sources); Assert.Equal("new transcript", reopened.ReadTranscript(id)); Assert.Contains("new notes", reopened.ReadNotes(id));
    }
    [Fact]
    public async Task ProfileLabelAndNotesModelChangesReuseActualAsrSelection()
    {
        using var store = Open(); store.UpdateSettings(new() { Models = new() { WhisperModel = "same.bin", WhisperProfile = "old label" } }); var job = store.Import(Audio()); Done(store, job.Id); store.Fail(job.Id, "retry fixture");
        store.UpdateSettings(new() { Models = store.Settings.Models with { WhisperProfile = "new label", NotesProfile = "new notes label", LlamaModel = "new.gguf" } }); store.Retry([job.Id], true);
        var engine = new FixtureEngine(); await new JobRunner(store, engine).RunNextAsync(); Assert.Equal(JobState.Completed, store.Get(job.Id).State); Assert.Equal("old transcript", engine.Sources); Assert.Equal("old transcript", store.ReadTranscript(job.Id));
    }
    [Fact]
    public async Task ChangedAsrCommitCreatesFreshExportAndPreservesPriorArchive()
    {
        using var store = Open(); store.UpdateSettings(new() { OutputDirectory = P("exports"), Models = new() { WhisperModel = "old.bin" } }); var job = store.Import(Audio()); Done(store, job.Id); var first = store.Export(job.Id); var originalZip = File.ReadAllBytes(first); store.Fail(job.Id, "retry fixture");
        store.UpdateSettings(store.Settings with { Models = store.Settings.Models with { WhisperModel = "new.bin" } }); store.Retry([job.Id], true); Assert.Null(store.Get(job.Id).ExportPath);
        await new JobRunner(store, new FixtureEngine()).RunNextAsync(); var second = store.Export(job.Id); Assert.NotEqual(first, second); Assert.Equal(originalZip, File.ReadAllBytes(first));
        using var archive = ZipFile.OpenRead(second); using var transcript = new StreamReader(archive.GetEntry("transcript.txt")!.Open()); using var notes = new StreamReader(archive.GetEntry("notes.md")!.Open()); Assert.Equal("new transcript", transcript.ReadToEnd()); Assert.Contains("new notes", notes.ReadToEnd());
    }
    [Fact]
    public void EverySuccessfulCommitInvalidatesThePriorExportReceipt()
    {
        using var store = Open(); store.UpdateSettings(new() { OutputDirectory = P("exports") }); var job = store.Import(Audio()); Done(store, job.Id); var first = store.Export(job.Id); var oldZip = File.ReadAllBytes(first);
        store.Fail(job.Id, "retry fixture"); store.Retry([job.Id]); Assert.True(store.TryStart(job.Id)); store.SaveTranscript(job.Id, "old transcript"); store.Complete(job.Id, "fresh notes");
        Assert.Null(store.Get(job.Id).ExportPath); var second = store.Export(job.Id); Assert.NotEqual(first, second); Assert.Equal(oldZip, File.ReadAllBytes(first));
    }
    [Fact]
    public async Task RestartBeforeChangedAsrStartsCannotReuseTheCommittedTranscript()
    {
        string id;
        using (var store = Open())
        {
            var job = store.Import(Audio()); id = job.Id; Done(store, id); store.Fail(id, "retry fixture"); store.UpdateSettings(new() { Models = new() { WhisperExecutable = "new-runtime" } }); store.Retry([id], true);
        }
        using var reopened = Open(); Assert.Equal("old transcript", reopened.ReadTranscript(id)); var engine = new FixtureEngine(); await new JobRunner(reopened, engine).RunNextAsync(); Assert.Equal("new transcript", engine.Sources); Assert.Equal("new transcript", reopened.ReadTranscript(id));
    }
    [Fact]
    public async Task FailedChangedAsrCommitKeepsOldPairAndOldPublishedResultAcrossRestart()
    {
        string id; string revision; string result; byte[] published;
        using (var store = Open())
        {
            var adapter = new MobileFolderAdapter(store); adapter.Register(P("sync")); var recording = Guid.NewGuid().ToString(); Request(store, recording, [1, 2, 3]); await adapter.ScanAsync(); var job = Assert.Single(store.Jobs); id = job.Id; Done(store, id); adapter.Publish(id); result = P("sync", "results", recording, job.AudioSha256, "result.json"); published = File.ReadAllBytes(result);
            store.Fail(id, "retry fixture"); store.UpdateSettings(store.Settings with { Models = new() { WhisperModel = "new.bin" } }); store.Retry([id], true); Assert.True(store.TryStart(id)); store.SaveTranscript(id, "new transcript"); revision = store.Get(id).PendingArtifactRevision!;
            File.Move(P("state", "library.json"), P("saved-library.json")); Directory.CreateDirectory(P("state", "library.json"));
            Assert.ThrowsAny<IOException>(() => store.Complete(id, "new notes")); Assert.Equal(JobState.GeneratingNotes, store.Get(id).State); Assert.Equal("old transcript", store.ReadTranscript(id)); Assert.Equal("old notes", store.ReadNotes(id)); Assert.Throws<InvalidOperationException>(() => adapter.Publish(id)); Assert.Equal(published, File.ReadAllBytes(result));
            Directory.Delete(P("state", "library.json")); File.Move(P("saved-library.json"), P("state", "library.json"));
        }
        using var reopened = Open(); Assert.Equal(JobState.Queued, reopened.Get(id).State); Assert.Equal(revision, reopened.Get(id).PendingArtifactRevision); Assert.Equal("old transcript", reopened.ReadTranscript(id)); Assert.Equal("old notes", reopened.ReadNotes(id)); Assert.Equal("new transcript", reopened.ReadTranscriptForProcessing(id)); Assert.Equal(published, File.ReadAllBytes(result));
        await new JobRunner(reopened, new FixtureEngine()).RunNextAsync(); Assert.Equal(JobState.Completed, reopened.Get(id).State); Assert.Null(reopened.Get(id).PendingArtifactRevision); Assert.Equal(revision, reopened.Get(id).ArtifactRevision); Assert.Equal("new transcript", reopened.ReadTranscript(id)); Assert.Contains("new notes", reopened.ReadNotes(id));
        Assert.Throws<IOException>(() => new MobileFolderAdapter(reopened).Publish(id)); Assert.Equal(published, File.ReadAllBytes(result));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NotesOnlyCommitFailurePreservesCommittedPairAndReusableAsrAcrossRestart(bool changeNotesModel, bool priorRevision)
    {
        string id; string revision; string archive; byte[] archived; string? committedRevision;
        using (var store = Open())
        {
            store.UpdateSettings(new() { OutputDirectory = P("exports") }); var job = store.Import(Audio()); id = job.Id; Done(store, id);
            if (priorRevision)
            {
                store.Fail(id, "revision fixture"); store.UpdateSettings(store.Settings with { Models = new() { WhisperModel = "current-asr.bin" } }); store.Retry([id], true); Assert.True(store.TryStart(id)); store.SaveTranscript(id, "old transcript"); store.Complete(id, "old notes");
            }
            committedRevision = store.Get(id).ArtifactRevision; archive = store.Export(id); archived = File.ReadAllBytes(archive); var oldWork = store.WorkDirectory(id); File.WriteAllText(Path.Combine(oldWork, "canary"), "keep old work"); store.Fail(id, "notes retry fixture");
            if (changeNotesModel) store.UpdateSettings(store.Settings with { Models = store.Settings.Models with { LlamaModel = "new-notes.gguf" } });
            store.Retry([id], changeNotesModel); Assert.True(store.TryStart(id)); Assert.Equal("old transcript", store.ReadTranscriptForProcessing(id)); store.SaveTranscript(id, "old transcript");
            File.Move(P("state", "library.json"), P("saved-library.json")); Directory.CreateDirectory(P("state", "library.json"));
            try
            {
                Assert.ThrowsAny<IOException>(() => store.Complete(id, "uncommitted NEW notes"));
                Assert.Equal("old transcript", store.ReadTranscript(id)); Assert.Equal("old notes", store.ReadNotes(id)); Assert.Equal(committedRevision, store.Get(id).ArtifactRevision); Assert.Equal("keep old work", File.ReadAllText(Path.Combine(oldWork, "canary"))); Assert.Equal(archived, File.ReadAllBytes(archive));
                revision = Assert.IsType<string>(store.Get(id).PendingArtifactRevision); Assert.NotEqual(committedRevision, revision);
            }
            finally { Directory.Delete(P("state", "library.json")); File.Move(P("saved-library.json"), P("state", "library.json")); }
        }
        using var reopened = Open(); Assert.Equal(JobState.Queued, reopened.Get(id).State); Assert.Equal(revision, reopened.Get(id).PendingArtifactRevision); Assert.Equal(committedRevision, reopened.Get(id).ArtifactRevision); Assert.Equal("old transcript", reopened.ReadTranscript(id)); Assert.Equal("old notes", reopened.ReadNotes(id)); Assert.Equal("old transcript", reopened.ReadTranscriptForProcessing(id));
        var engine = new FixtureEngine(); await new JobRunner(reopened, engine).RunNextAsync(); Assert.Equal(0, engine.AsrCalls); Assert.Equal("old transcript", engine.Sources); Assert.Equal(JobState.Completed, reopened.Get(id).State); Assert.Equal(revision, reopened.Get(id).ArtifactRevision); Assert.Null(reopened.Get(id).PendingArtifactRevision); Assert.Equal("old transcript", reopened.ReadTranscript(id)); Assert.Contains("new notes", reopened.ReadNotes(id)); Assert.Equal(archived, File.ReadAllBytes(archive)); Assert.NotEqual(archive, reopened.Export(id));
    }
    [Fact]
    public async Task RetryingEmptyChangedAsrStageCannotSeedItFromTheOldCommittedTranscript()
    {
        string id; string revision;
        using (var store = Open())
        {
            var job = store.Import(Audio()); id = job.Id; Done(store, id); store.Fail(id, "fixture"); store.UpdateSettings(new() { Models = new() { WhisperModel = "new-asr.bin" } }); store.Retry([id], true); revision = store.Get(id).PendingArtifactRevision!; store.CancelQueued([id]);
            store.UpdateSettings(store.Settings with { Models = store.Settings.Models with { LlamaModel = "new-notes.gguf" } }); store.Retry([id], true);
            Assert.Equal(revision, store.Get(id).PendingArtifactRevision); Assert.Equal("", store.ReadTranscriptForProcessing(id)); Assert.Equal("old transcript", store.ReadTranscript(id)); Assert.Equal("old notes", store.ReadNotes(id));
        }
        using var reopened = Open(); var engine = new FixtureEngine(); await new JobRunner(reopened, engine).RunNextAsync(); Assert.Equal(1, engine.AsrCalls); Assert.Equal("new transcript", engine.Sources); Assert.Equal("new transcript", reopened.ReadTranscript(id));
    }
    [Theory]
    [InlineData("WhisperExecutable")]
    [InlineData("WhisperModel")]
    [InlineData("FfmpegExecutable")]
    [InlineData("Compute")]
    public async Task ActualAsrSelectionChangesUseFreshWorkAndTranscription(string field)
    {
        using var store = Open(); var job = store.Import(Audio()); Done(store, job.Id); store.Fail(job.Id, "retry fixture"); var oldWork = store.WorkDirectory(job.Id); File.WriteAllText(Path.Combine(oldWork, "canary"), "keep");
        var models = store.Settings.Models; models = field switch { "WhisperExecutable" => models with { WhisperExecutable = "new-runtime" }, "WhisperModel" => models with { WhisperModel = "new.bin" }, "FfmpegExecutable" => models with { FfmpegExecutable = "new-decoder" }, _ => models with { Compute = "CUDA", CudaConfirmed = true } };
        store.UpdateSettings(store.Settings with { Models = models }); store.Retry([job.Id], true); Assert.NotEqual(oldWork, store.WorkDirectory(job.Id)); Assert.Empty(Directory.EnumerateFileSystemEntries(store.WorkDirectory(job.Id))); Assert.Equal("keep", File.ReadAllText(Path.Combine(oldWork, "canary")));
        var engine = new FixtureEngine(); await new JobRunner(store, engine).RunNextAsync(); Assert.Equal(JobState.Completed, store.Get(job.Id).State); Assert.Equal("new transcript", engine.Sources);
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("same-as-committed")]
    public void ReopenedLibraryRejectsUnsafeArtifactRevisionSelection(string value)
    {
        string id;
        using (var store = Open()) { var job = store.Import(Audio()); id = job.Id; }
        var path = P("state", "library.json"); var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; var row = json["Jobs"]![0]!;
        row["ArtifactRevision"] = id; row["PendingArtifactRevision"] = value == "same-as-committed" ? id : value; File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => Open()); Assert.True(File.Exists(Item(id, "original.wav")));
    }
    [Fact]
    public async Task MobileIdentityIsEnforcedAtomicallyAtTheImportBoundary()
    {
        using var store = Open(); var audio = Audio(); var origin = new MobileOrigin(P("sync"), Guid.NewGuid().ToString(), "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", store.ProcessorId, "2026-10-18T01:02:03+09:00", "fixture");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => store.Import(audio, origin.OriginalTitle, origin.RecordedAt, origin)))));
        Assert.Single(results, e => e == null); Assert.Single(results, e => e is InvalidDataException); Assert.Single(store.Jobs); Assert.Single(Directory.EnumerateDirectories(P("state", "items")));
    }
    sealed class FixtureEngine(bool wait = false) : IInferenceEngine
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Sources { get; private set; }
        public int AsrCalls { get; private set; }
        public Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct) { AsrCalls++; return Task.FromResult("new transcript"); }
        public async Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
        {
            Sources = string.Join("\n", sources); Entered.TrySetResult(); if (wait) await Task.Delay(Timeout.Infinite, ct);
            return "{\"schema_version\":1,\"topics\":[{\"heading\":\"fresh\",\"summary\":\"new notes\",\"conditions\":[],\"examples\":[],\"refs\":[1]}]}";
        }
    }
}
