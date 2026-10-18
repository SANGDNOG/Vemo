using Avalonia.Input;
using Vemo.Core;

namespace Vemo.App;

// The format never leaves this process. Source identity also prevents another window's IDs being used.
public sealed record LibraryDragPayload(object Source, string? FolderId, string[] RecordingIds);

public static class LibraryDropRules
{
    public static readonly DataFormat<LibraryDragPayload> Format = DataFormat.CreateInProcessFormat<LibraryDragPayload>("Vemo.LibraryDrag");

    public static bool CanMoveFolder(LibraryStore store, string id, string? destination)
    {
        var folders = store.Folders;
        var moving = folders.FirstOrDefault(f => f.Id == id);
        if (moving == null || moving.ParentId == destination) return false;
        var seen = new HashSet<string>();
        for (var parent = destination; parent != null;)
        {
            if (parent == id || !seen.Add(parent)) return false;
            var folder = folders.FirstOrDefault(f => f.Id == parent);
            if (folder == null) return false;
            parent = folder.ParentId;
        }
        return !folders.Any(f => f.Id != id && f.ParentId == destination && f.Name.Equals(moving.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanMoveRecordings(LibraryStore store, IReadOnlyList<string> ids, string? destination)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count || destination != null && !store.Folders.Any(f => f.Id == destination)) return false;
        var jobs = store.Jobs;
        return ids.All(id => jobs.Any(j => j.Id == id && !j.Trashed && j.State != JobState.Purged))
            && ids.Any(id => jobs.First(j => j.Id == id).FolderId != destination);
    }

    public static bool CanImportFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        try
        {
            foreach (var path in paths)
            {
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
                SafeFiles.NoLinks(path);
                if (!new[] { ".wav", ".mp3", ".m4a", ".mp4", ".flac", ".ogg", ".aac", ".wma" }.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
                if (new FileInfo(path).Length is <= 0 or > 2147483648) return false;
                SafeFiles.Title(Path.GetFileNameWithoutExtension(path));
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
