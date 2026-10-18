using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class WatchedFolderImporterTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-watch-" + Guid.NewGuid());
    string Input => Path.Combine(root, "input");
    string State => Path.Combine(root, "state");
    DateTimeOffset now = DateTimeOffset.UtcNow;
    public WatchedFolderImporterTests() => Directory.CreateDirectory(Input);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    WatchedFolderImporter Watch(LibraryStore store) => new(store, () => now);
    string Audio(string name = "Meeting.wav", byte value = 1) { var path = Path.Combine(Input, name); File.WriteAllBytes(path, [value, 2, 3]); return path; }
    [Fact]
    public async Task StableTwoScansImportOwnedAudioWithoutInventingDateAndDedupeAcrossRestart()
    {
        var source = Audio(); string owned;
        using (var store = new LibraryStore(State))
        {
            var watcher = Watch(store);
            Assert.Equal(1, (await watcher.ScanAsync(Input)).Waiting);
            now += TimeSpan.FromSeconds(4); Assert.Equal(0, (await watcher.ScanAsync(Input)).Imported);
            now += TimeSpan.FromSeconds(1); Assert.Equal(1, (await watcher.ScanAsync(Input)).Imported);
            var job = Assert.Single(store.Jobs); Assert.Equal("Meeting", job.Title); Assert.Null(job.RecordedAt); Assert.Null(job.Mobile);
            owned = store.AudioPath(job.Id); Assert.NotEqual(source, owned);
            Assert.Equal(0, (await watcher.ScanAsync(Input)).Imported);
            Assert.True(store.TryStart(job.Id)); // The queue runner does not change import identity.
            var scans = await Task.WhenAll(Watch(store).ScanAsync(Input), watcher.ScanAsync(Input));
            Assert.All(scans, scan => Assert.Equal(0, scan.Imported));
        }
        using var reopened = new LibraryStore(State); var restarted = Watch(reopened);
        await restarted.ScanAsync(Input); now += TimeSpan.FromSeconds(5);
        Assert.Equal(0, (await restarted.ScanAsync(Input)).Imported); Assert.Single(reopened.Jobs);
        File.Delete(source); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(owned));
    }
    [Fact]
    public async Task ChangedContentResetsStabilityAndCreatesNewCopyEvenWithSameSizeAndTimestamp()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store); var path = Audio();
        await watcher.ScanAsync(Input); now += TimeSpan.FromSeconds(5); await watcher.ScanAsync(Input);
        var original = Assert.Single(store.Jobs); var stamp = File.GetLastWriteTimeUtc(path);
        Audio(value: 9); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(1, (await watcher.ScanAsync(Input)).Waiting);
        now += TimeSpan.FromSeconds(5); Assert.Equal(1, (await watcher.ScanAsync(Input)).Imported);
        Assert.Equal(2, store.Jobs.Count); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.AudioPath(original.Id)));
        Audio(); File.SetLastWriteTimeUtc(path, stamp); await watcher.ScanAsync(Input); now += TimeSpan.FromSeconds(5);
        Assert.Equal(0, (await watcher.ScanAsync(Input)).Imported); Assert.Equal(2, store.Jobs.Count);
    }
    [Fact]
    public async Task TopLevelOnlyAndMissingFilesDoNotCarryStability()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store); var path = Audio();
        Audio("partial.wav.tmp"); Directory.CreateDirectory(Path.Combine(Input, "nested"));
        File.WriteAllBytes(Path.Combine(Input, "nested", "nested.wav"), [1]);
        await watcher.ScanAsync(Input); File.Delete(path); now += TimeSpan.FromSeconds(5); await watcher.ScanAsync(Input);
        Audio(); Assert.Equal(1, (await watcher.ScanAsync(Input)).Waiting);
        now += TimeSpan.FromSeconds(5); Assert.Equal(1, (await watcher.ScanAsync(Input)).Imported); Assert.Single(store.Jobs);
    }
    [Fact]
    public async Task UnsafeDirectoriesAndUnavailableFilesAreReportedAndCancellationPropagates()
    {
        using var store = new LibraryStore(State); var output = Path.Combine(root, "output"); var mobile = Path.Combine(root, "mobile");
        Directory.CreateDirectory(output); Directory.CreateDirectory(mobile);
        store.UpdateSettings(store.Settings with { OutputDirectory = output, MobileDirectory = mobile }); var watcher = Watch(store);
        foreach (var dir in new[] { State, root, output, mobile, Path.Combine(root, "missing") })
            Assert.NotEmpty((await watcher.ScanAsync(dir)).Issues);
        File.WriteAllText(Path.Combine(Input, "processor.json"), "{}");
        Assert.NotEmpty((await watcher.ScanAsync(Input)).Issues); File.Delete(Path.Combine(Input, "processor.json"));
        var path = Audio(); using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.NotEmpty((await watcher.ScanAsync(Input)).Issues);
        File.WriteAllBytes(Path.Combine(Input, "empty.wav"), []);
        Assert.NotEmpty((await watcher.ScanAsync(Input)).Issues);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.ScanAsync(Input, cancelled.Token)); Assert.Empty(store.Jobs);
    }
    [Fact]
    public async Task SymbolicLinksAreRejected()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store); var source = Audio();
        File.CreateSymbolicLink(Path.Combine(Input, "link.wav"), source);
        Assert.NotEmpty((await watcher.ScanAsync(Input)).Issues); now += TimeSpan.FromSeconds(5);
        Assert.Equal(1, (await watcher.ScanAsync(Input)).Imported); Assert.Single(store.Jobs);
    }
    [Fact]
    public async Task IdenticalBytesAtDifferentPathsAreDistinctAndPurgedReceiptsSuppressReimport()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store); Audio(); Audio("Other.WAV");
        await watcher.ScanAsync(Path.Combine(Input, ".")); now += TimeSpan.FromSeconds(5);
        Assert.Equal(2, (await watcher.ScanAsync(Input)).Imported);
        foreach (var job in store.Jobs) { store.CancelQueued([job.Id]); store.Trash([job.Id]); store.Purge([job.Id]); }
        Assert.Equal(0, (await watcher.ScanAsync(Input)).Imported); Assert.Equal(2, store.Jobs.Count);
    }
    [Fact]
    public async Task FailedReceiptWriteDoesNotImportAndCanBeRetried()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store); Audio();
        await watcher.ScanAsync(Input); now += TimeSpan.FromSeconds(5);
        var receipts = Path.Combine(State, "watched-imports"); File.WriteAllText(receipts, "blocked");
        Assert.NotEmpty((await watcher.ScanAsync(Input)).Issues); Assert.Empty(store.Jobs);
        File.Delete(receipts); await watcher.ScanAsync(Input); now += TimeSpan.FromSeconds(5);
        Assert.Equal(1, (await watcher.ScanAsync(Input)).Imported); Assert.Single(store.Jobs);
    }
    [Fact]
    public async Task InterruptedReceiptCommitRecoversExistingJobAndCorruptReceiptFailsClosed()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store); Audio();
        await watcher.ScanAsync(Input); now += TimeSpan.FromSeconds(5); await watcher.ScanAsync(Input);
        var receipt = Assert.Single(Directory.GetFiles(Path.Combine(State, "watched-imports")));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(receipt))!;
        json["JobId"] = null; File.WriteAllText(receipt, json.ToJsonString());
        var restarted = Watch(store); await restarted.ScanAsync(Input); now += TimeSpan.FromSeconds(5);
        Assert.Equal(0, (await restarted.ScanAsync(Input)).Imported); Assert.Single(store.Jobs);
        File.WriteAllText(receipt, "broken json");
        Assert.NotEmpty((await restarted.ScanAsync(Input)).Issues); Assert.Single(store.Jobs);
    }
    [Fact]
    public async Task OversizedAudioIsRejectedBeforeReadingItsContents()
    {
        using var store = new LibraryStore(State); var watcher = Watch(store);
        using (var audio = File.Create(Path.Combine(Input, "oversized.wav"))) audio.SetLength(2147483649);
        Assert.Single((await watcher.ScanAsync(Input)).Issues); Assert.Empty(store.Jobs);
    }
    [Fact]
    public async Task ConcurrentStableWatchersShareImportGate()
    {
        using var store = new LibraryStore(State); var first = Watch(store); var second = Watch(store); Audio();
        await first.ScanAsync(Input); await second.ScanAsync(Input); now += TimeSpan.FromSeconds(5);
        var scans = await Task.WhenAll(first.ScanAsync(Input), second.ScanAsync(Input));
        Assert.Equal(1, scans.Sum(scan => scan.Imported)); Assert.Single(store.Jobs);
    }
}
