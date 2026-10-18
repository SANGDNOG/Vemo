using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class LifecycleTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-tests-" + Guid.NewGuid());
    string State => Path.Combine(root, "state");
    string Output => Path.Combine(root, "output");
    string Audio { get { Directory.CreateDirectory(root); var p = Path.Combine(root, "source.wav"); File.WriteAllBytes(p, [1, 2, 3, 4]); return p; } }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    LibraryStore Open() => new(State);
    [Fact]
    public void ImportSnapshotsModelsAndDateAndRecoversInterruptedJob()
    {
        string id;
        using (var s = Open())
        {
            s.UpdateSettings(new() { OutputDirectory = Output, Models = new() { WhisperProfile = "small" } });
            var j = s.Import(Audio, "회의", "2026-10-17T00:30:00+14:00"); id = j.Id;
            s.UpdateSettings(new() { OutputDirectory = Output, Models = new() { WhisperProfile = "large-v3-turbo" } });
            Assert.Equal("small", s.Get(id).Models.WhisperProfile);
            Assert.Equal("2026-10-17T00:30:00+14:00", j.RecordedAt);
            Assert.True(s.TryStart(id));
        }
        using var reopened = Open(); Assert.Equal(JobState.Queued, reopened.Get(id).State);
        Assert.Equal("small", reopened.Get(id).Models.WhisperProfile);
    }
    [Fact]
    public void NestedMoveTrashRestorePurgeOnlyOwnedAndPreserveArchives()
    {
        using var s = Open(); s.UpdateSettings(new() { OutputDirectory = Output });
        var a = s.CreateFolder("수업", null); var b = s.CreateFolder("Week 1", a.Id);
        Assert.ThrowsAny<Exception>(() => s.MoveFolder(a.Id, b.Id));
        var source = Audio; var j = s.Import(source); s.Move([j.Id], b.Id);
        Complete(s, j.Id); var zip = s.Export(j.Id);
        Assert.Contains(Path.Combine("수업", "Week 1"), zip);
        using (var archive = ZipFile.OpenRead(zip)) Assert.Equal(new[] { "notes.md", "original.wav", "transcript.txt" }, archive.Entries.Select(x => x.FullName).Order().ToArray());
        s.Trash([j.Id]); s.Restore([j.Id]); Assert.Equal(b.Id, s.Get(j.Id).FolderId);
        s.Trash([j.Id]); s.Purge([j.Id]); Assert.Equal(JobState.Purged, s.Get(j.Id).State);
        Assert.True(File.Exists(source)); Assert.True(File.Exists(zip));
        Assert.False(Directory.Exists(Path.Combine(State, "items", j.Id)));
    }
    [Fact]
    public void CollisionNeverOverwritesAndUnknownDateNotMtime()
    {
        using var s = Open(); s.UpdateSettings(new() { OutputDirectory = Output });
        var a = s.Import(Audio, "Same"); var b = s.Import(Audio, "Same");
        Assert.Null(a.RecordedAt); Complete(s, a.Id); Complete(s, b.Id);
        var p = s.Export(a.Id); var bytes = File.ReadAllBytes(p); var q = s.Export(b.Id);
        Assert.NotEqual(p, q); Assert.Equal(bytes, File.ReadAllBytes(p)); Assert.Equal(p, s.Export(a.Id));
    }
    [Fact]
    public void TraversalSymlinkAndOverlappingOutputRejected()
    {
        using var s = Open();
        Assert.ThrowsAny<Exception>(() => s.CreateFolder("../escape", null));
        Assert.ThrowsAny<Exception>(() => s.UpdateSettings(new() { OutputDirectory = Path.Combine(State, "exports") }));
        var target = Audio; var link = Path.Combine(root, "linked.wav"); File.CreateSymbolicLink(link, target);
        Assert.ThrowsAny<Exception>(() => s.Import(link));
        var j = s.Import(target); var owned = Path.Combine(State, "items", j.Id);
        Directory.Delete(owned, true); Directory.CreateSymbolicLink(owned, root);
        s.Trash([j.Id]); Assert.ThrowsAny<Exception>(() => s.Purge([j.Id])); Assert.True(File.Exists(target));
        Directory.Delete(owned);
    }
    [Fact]
    public async Task InvalidNotesPreserveAsrAndRetryAndCancellationAreRealStates()
    {
        using var s = Open(); var j = s.Import(Audio);
        var runner = new JobRunner(s, new FakeEngine(invalid: true)); await runner.RunNextAsync();
        Assert.Equal(JobState.Failed, s.Get(j.Id).State); Assert.Equal("실제 전사 대신 테스트 전사", s.ReadTranscript(j.Id));
        s.Retry([j.Id]); var cancelEngine = new FakeEngine(wait: true); var r = new JobRunner(s, cancelEngine);
        using var cts = new CancellationTokenSource(); var running = r.RunNextAsync(cts.Token);
        await cancelEngine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel(); await running;
        Assert.Equal(JobState.Cancelled, s.Get(j.Id).State);
        s.Retry([j.Id]); await new JobRunner(s, new FakeEngine()).RunNextAsync(); Assert.Equal(JobState.Completed, s.Get(j.Id).State);
    }
    [Fact]
    public void NotesRequireSchemaAndValidReferences()
    {
        Assert.ThrowsAny<Exception>(() => NotesDocument.Parse("{\"schema_version\":1,\"topics\":[{\"heading\":\"X\",\"summary\":\"Y\",\"conditions\":[],\"examples\":[],\"refs\":[99]}]}", 1));
        Assert.ThrowsAny<Exception>(() => NotesDocument.Parse("{\"schema_version\":1,\"topics\":[]}", 1));
    }
    static void Complete(LibraryStore s, string id) { Assert.True(s.TryStart(id)); s.SaveTranscript(id, "hello"); s.Complete(id, "# Notes\nhello"); }
    sealed class FakeEngine(bool invalid = false, bool wait = false) : IInferenceEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct) { Started.TrySetResult(); if (wait) await Task.Delay(Timeout.Infinite, ct); return "실제 전사 대신 테스트 전사"; }
        public async Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct) { Started.TrySetResult(); if (wait) await Task.Delay(Timeout.Infinite, ct); return invalid ? "{}" : "{\"schema_version\":1,\"topics\":[{\"heading\":\"주제\",\"summary\":\"설명\",\"conditions\":[],\"examples\":[],\"refs\":[1]}]}"; }
    }
}
