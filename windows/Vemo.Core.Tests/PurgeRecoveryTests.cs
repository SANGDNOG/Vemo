using System.Text.Json;
using System.Text.Json.Nodes;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class PurgeRecoveryTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-purge-" + Guid.NewGuid());
    string P(params string[] parts) => Path.Combine([root, .. parts]);
    string Item(string id) => P("state", "items", id);
    string Obstacle(string id) => Path.Combine(Item(id), "stop-deletion");
    LibraryStore Open() => new(P("state"));
    public PurgeRecoveryTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    (string Id, string Source, string Export) CompletedTrash(LibraryStore store)
    {
        var source = P("source.wav"); File.WriteAllBytes(source, [1, 2, 3, 4]);
        store.UpdateSettings(new() { OutputDirectory = P("exports") });
        var origin = new MobileOrigin(P("sync"), Guid.NewGuid().ToString(), SafeFiles.Hash(source), store.ProcessorId, "2026-10-18T01:02:03+09:00", "fixture");
        var job = store.Import(source, origin.OriginalTitle, origin.RecordedAt, origin);
        Assert.True(store.TryStart(job.Id)); store.SaveTranscript(job.Id, "transcript"); store.Complete(job.Id, "notes");
        var export = store.Export(job.Id); store.Trash([job.Id]);
        return (job.Id, source, export);
    }

    void BlockDeletion(string id, string source) => File.CreateSymbolicLink(Obstacle(id), source);
    void AssertPending(LibraryStore store, string id)
    {
        var job = Assert.Single(store.Jobs, j => j.Id == id && j.State == JobState.Purged && !string.IsNullOrWhiteSpace(j.Error));
        Assert.True(job.Trashed);
        using var persisted = JsonDocument.Parse(File.ReadAllText(P("state", "library.json")));
        var row = Assert.Single(persisted.RootElement.GetProperty("Jobs").EnumerateArray(), j => j.GetProperty("Id").GetString() == id);
        Assert.Equal("Purged", row.GetProperty("State").GetString());
        Assert.Equal(job.Error, row.GetProperty("Error").GetString());
    }

    [Fact]
    public void FailedPurgeRemainsDiscoverableAndRetryableAcrossReopen()
    {
        string id; string source; string export; byte[] archived;
        using (var store = Open())
        {
            (id, source, export) = CompletedTrash(store); archived = File.ReadAllBytes(export); BlockDeletion(id, source);
            Assert.Throws<IOException>(() => store.Purge([id]));
            AssertPending(store, id);
            Assert.Throws<IOException>(() => store.Purge([id])); AssertPending(store, id);
        }
        using (var reopened = Open())
        {
            AssertPending(reopened, id);
            Assert.Throws<IOException>(() => reopened.Purge([id])); AssertPending(reopened, id);
            File.Delete(Obstacle(id)); reopened.Purge([id]);
            Assert.Equal(JobState.Purged, reopened.Get(id).State); Assert.Null(reopened.Get(id).Error); Assert.False(Directory.Exists(Item(id)));
        }
        using var finished = Open(); Assert.Equal(JobState.Purged, finished.Get(id).State); Assert.Null(finished.Get(id).Error);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source)); Assert.Equal(archived, File.ReadAllBytes(export));
    }

    [Fact]
    public void PartialOwnedTreeCannotBeRestoredProcessedExportedOrReimported()
    {
        string id; string source; string export; byte[] archived; MobileOrigin origin;
        using (var store = Open())
        {
            (id, source, export) = CompletedTrash(store); archived = File.ReadAllBytes(export); origin = store.Get(id).Mobile!;
            BlockDeletion(id, source); Assert.Throws<IOException>(() => store.Purge([id]));
            // Model interruption after only a subset of owned files was removed, without relying on enumeration order.
            File.Delete(Path.Combine(Item(id), "original.wav"));
            Assert.False(File.Exists(Path.Combine(Item(id), "original.wav"))); Assert.True(Directory.Exists(Item(id)));
        }
        using var reopened = Open(); AssertPending(reopened, id);
        reopened.Restore([id]); reopened.Retry([id], true);
        Assert.True(reopened.Get(id).Trashed); Assert.Equal(JobState.Purged, reopened.Get(id).State); Assert.False(reopened.TryStart(id));
        Assert.Throws<InvalidOperationException>(() => reopened.Export(id));
        Assert.Throws<InvalidOperationException>(() => new MobileFolderAdapter(reopened).Publish(id));
        Assert.Throws<InvalidDataException>(() => reopened.Import(source, origin.OriginalTitle, origin.RecordedAt, origin));
        Assert.False(File.Exists(Path.Combine(Item(id), "original.wav")));
        File.Delete(Obstacle(id)); reopened.Purge([id]); Assert.Null(reopened.Get(id).Error); Assert.False(Directory.Exists(Item(id)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source)); Assert.Equal(archived, File.ReadAllBytes(export));
    }

    [Fact]
    public void ReopenFinishesDeletionAndDurablyClearsPendingError()
    {
        string id; string source; string export; byte[] archived;
        using (var store = Open())
        {
            (id, source, export) = CompletedTrash(store); archived = File.ReadAllBytes(export); BlockDeletion(id, source);
            Assert.Throws<IOException>(() => store.Purge([id])); AssertPending(store, id);
        }
        File.Delete(Obstacle(id));
        using (var recovered = Open())
        {
            Assert.Equal(JobState.Purged, recovered.Get(id).State); Assert.Null(recovered.Get(id).Error); Assert.False(Directory.Exists(Item(id)));
            using var persisted = JsonDocument.Parse(File.ReadAllText(P("state", "library.json")));
            Assert.Equal(JsonValueKind.Null, persisted.RootElement.GetProperty("Jobs")[0].GetProperty("Error").ValueKind);
        }
        using var reopened = Open(); Assert.Null(reopened.Get(id).Error); Assert.False(Directory.Exists(Item(id)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source)); Assert.Equal(archived, File.ReadAllBytes(export));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Old processing failure")]
    public void LegacyTombstoneWithBlockedLeftoversBecomesDiscoverable(string? previousError)
    {
        string id; string source;
        using (var store = Open())
        {
            (id, source, _) = CompletedTrash(store); BlockDeletion(id, source);
        }
        var manifest = P("state", "library.json"); var json = JsonNode.Parse(File.ReadAllText(manifest))!;
        json["Jobs"]![0]!["State"] = "Purged"; json["Jobs"]![0]!["Error"] = previousError; File.WriteAllText(manifest, json.ToJsonString());
        using var reopened = Open(); AssertPending(reopened, id); Assert.NotEqual(previousError, reopened.Get(id).Error);
        File.Delete(Obstacle(id)); reopened.Purge([id]); Assert.Null(reopened.Get(id).Error); Assert.False(Directory.Exists(Item(id)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
    }

    [Fact]
    public void OwnedDirectoryReparsePointRemainsPendingUntilRepaired()
    {
        string id; string source;
        using (var store = Open())
        {
            (id, source, _) = CompletedTrash(store); Directory.Delete(Item(id), true); Directory.CreateSymbolicLink(Item(id), root);
            Assert.Throws<IOException>(() => store.Purge([id])); AssertPending(store, id);
        }
        using var reopened = Open(); AssertPending(reopened, id); Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
        Directory.Delete(Item(id)); reopened.Purge([id]); Assert.Null(reopened.Get(id).Error); Assert.False(Directory.Exists(Item(id)));
    }

    [Fact]
    public void FailedIntentWritePreservesOwnedFilesAndOriginalJobState()
    {
        using var store = Open(); var (id, source, export) = CompletedTrash(store); var archived = File.ReadAllBytes(export);
        var manifest = P("state", "library.json"); File.Move(manifest, P("saved-library.json")); Directory.CreateDirectory(manifest);
        try
        {
            var error = Record.Exception(() => store.Purge([id])); Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal(JobState.Completed, store.Get(id).State); Assert.True(store.Get(id).Trashed); Assert.Null(store.Get(id).Error);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(Item(id), "original.wav")));
            Assert.Equal("transcript", store.ReadTranscript(id)); Assert.Equal("notes", store.ReadNotes(id));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source)); Assert.Equal(archived, File.ReadAllBytes(export));
        }
        finally { Directory.Delete(manifest); File.Move(P("saved-library.json"), manifest); }
    }
}
