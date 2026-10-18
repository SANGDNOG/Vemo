using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Vemo.Core;

public sealed record WatchedFolderIssue(string Path, string Message);
public sealed record WatchedFolderScan(int Imported, int Waiting, IReadOnlyList<WatchedFolderIssue> Issues);

public sealed class WatchedFolderImporter
{
    static readonly ConditionalWeakTable<LibraryStore, SemaphoreSlim> Gates = new();
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".m4a", ".mp4", ".flac", ".ogg", ".aac", ".wma" };
    readonly LibraryStore store;
    readonly Func<DateTimeOffset> clock;
    readonly Dictionary<string, Observation> observed = new(StringComparer.OrdinalIgnoreCase);
    sealed record Observation(long Length, DateTime LastWrite, string Hash, DateTimeOffset Since);
    sealed record Receipt(string Path, string Hash, string? JobId, string[] Before);
    public WatchedFolderImporter(LibraryStore store) : this(store, () => DateTimeOffset.UtcNow) { }
    public WatchedFolderImporter(LibraryStore store, Func<DateTimeOffset> clock) { this.store = store; this.clock = clock; }

    public async Task<WatchedFolderScan> ScanAsync(string directory, CancellationToken ct = default)
    {
        var gate = Gates.GetValue(store, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            int imported = 0, waiting = 0; var issues = new List<WatchedFolderIssue>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) { observed.Clear(); return new(0, 0, []); }
                directory = SafeFiles.Full(directory); SafeFiles.Disjoint(directory, store.Root);
                var settings = store.Settings;
                if (settings.OutputDirectory != "") SafeFiles.Disjoint(directory, settings.OutputDirectory);
                if (settings.MobileDirectory != "") SafeFiles.Disjoint(directory, settings.MobileDirectory);
                RejectProtocol(directory);
                if (!Directory.Exists(directory)) throw new IOException("Input directory is unavailable.");
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Extensions.Contains(Path.GetExtension(path))) continue;
                    seen.Add(path);
                    try
                    {
                        SafeFiles.NoLinks(path); var info = new FileInfo(path); info.Refresh();
                        if ((info.Attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint | FileAttributes.Temporary | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                            throw new IOException("Audio is not available as a completed local file.");
                        if (info.Length is <= 0 or > 2147483648) throw new IOException("Audio must be nonempty and at most 2 GiB.");
                        var length = info.Length; var write = info.LastWriteTimeUtc;
                        // Keep the read handle until import completes, denying writers/deletion on Windows.
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                        info.Refresh(); SafeFiles.NoLinks(path);
                        if (info.Length != length || info.LastWriteTimeUtc != write) { observed.Remove(path); waiting++; continue; }
                        var now = clock();
                        if (!observed.TryGetValue(path, out var previous) || previous.Length != length || previous.LastWrite != write || previous.Hash != hash)
                        { observed[path] = new(length, write, hash, now); waiting++; continue; }
                        if (now - previous.Since < TimeSpan.FromSeconds(5)) { waiting++; continue; }
                        ct.ThrowIfCancellationRequested();
                        // Windows path identity is case-insensitive. Content revisions have separate receipts.
                        var normalized = SafeFiles.Full(path).ToUpperInvariant();
                        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\n" + hash)));
                        var receiptPath = Path.Combine(store.Root, "watched-imports", key + ".json"); SafeFiles.NoLinks(receiptPath);
                        Receipt receipt;
                        if (File.Exists(receiptPath))
                        {
                            using var doc = JsonFormat.Strict(SafeFiles.Read(receiptPath));
                            receipt = doc.Deserialize<Receipt>() ?? throw new InvalidDataException("Invalid watched import receipt.");
                            if (receipt.Path != normalized || receipt.Hash != hash || receipt.Before == null) throw new InvalidDataException("Watched import receipt identity mismatch.");
                            if (receipt.JobId != null) continue; // Trash/purge must not cause another import.
                            // Reconcile an import interrupted between the library commit and receipt commit.
                            var recovered = store.Jobs.FirstOrDefault(j => !receipt.Before.Contains(j.Id) && j.AudioSha256 == hash && j.Mobile == null);
                            if (recovered != null) { SaveReceipt(receiptPath, receipt with { JobId = recovered.Id }); continue; }
                        }
                        else
                        {
                            receipt = new(normalized, hash, null, store.Jobs.Where(j => j.AudioSha256 == hash).Select(j => j.Id).ToArray());
                            SaveReceipt(receiptPath, receipt);
                        }
                        ct.ThrowIfCancellationRequested();
                        var job = store.Import(path, Path.GetFileNameWithoutExtension(path));
                        imported++;
                        // Import verifies its owned copy against the source. A change since observation waits for a fresh scan.
                        if (job.AudioSha256 != hash)
                        {
                            // Retain the actual import receipt too, so the next scan cannot duplicate this revision.
                            var actualKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\n" + job.AudioSha256)));
                            SaveReceipt(Path.Combine(store.Root, "watched-imports", actualKey + ".json"), receipt with { Hash = job.AudioSha256, JobId = job.Id });
                            throw new IOException("Audio changed during import; a copy was retained. Wait for the file to finish writing.");
                        }
                        SaveReceipt(receiptPath, receipt with { JobId = job.Id });
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
                    { observed.Remove(path); issues.Add(new(path, e.Message)); }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { observed.Clear(); issues.Add(new(directory, e.Message)); }
            foreach (var stale in observed.Keys.Where(path => !seen.Contains(path)).ToArray()) observed.Remove(stale);
            ct.ThrowIfCancellationRequested();
            return new(imported, waiting, issues);
        }
        finally { gate.Release(); }
    }
    static void SaveReceipt(string path, Receipt receipt) => SafeFiles.Atomic(path, JsonSerializer.Serialize(receipt));
    static void RejectProtocol(string directory)
    {
        // Also reject a selected subdirectory of an unconfigured mobile protocol root.
        for (string? current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            SafeFiles.NoLinks(current);
            foreach (var marker in new[] { "processor.json", "recording.json" })
            {
                var path = Path.Combine(current, marker); SafeFiles.NoLinks(path);
                if (File.Exists(path)) throw new IOException("Mobile protocol directories cannot be watched as ordinary audio folders.");
            }
        }
    }
}
