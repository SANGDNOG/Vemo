using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class FolderManagementTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-folders-" + Guid.NewGuid());
    string State => Path.Combine(root, "state");
    string Output => Path.Combine(root, "exports");
    public FolderManagementTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    LibraryStore Open() => new(State);
    RecordingJob Import(LibraryStore store, string? folder)
    {
        var source = Path.Combine(root, "source.wav"); File.WriteAllBytes(source, [1, 2, 3]);
        return store.Import(source, "Recording", folderId: folder);
    }
    RecordingJob Exported(LibraryStore store, string? folder)
    {
        var job = Import(store, folder); Assert.True(store.TryStart(job.Id));
        store.SaveTranscript(job.Id, "transcript"); store.Complete(job.Id, "notes"); store.Export(job.Id);
        return store.Get(job.Id);
    }
    [Theory]
    [InlineData("rename")]
    [InlineData("move")]
    [InlineData("delete")]
    public void NestedChangesPreserveJobsArtifactsTrashAndArchivesAcrossReload(string operation)
    {
        RecordingJob[] before; LibraryFolder parent, child, grandchild, other; string freshExport, activeId, nestedId;
        var archives = new Dictionary<string, byte[]>();
        using (var store = Open())
        {
            store.UpdateSettings(new() { OutputDirectory = Output });
            parent = store.CreateFolder("Parent", null); child = store.CreateFolder("Child", parent.Id);
            grandchild = store.CreateFolder("Grandchild", child.Id); other = store.CreateFolder("Other", null);
            var direct = Exported(store, parent.Id); var nested = Exported(store, grandchild.Id);
            var trash = Exported(store, child.Id); store.Trash([trash.Id]);
            var purged = Import(store, grandchild.Id); store.CancelQueued([purged.Id]); store.Trash([purged.Id]); store.Purge([purged.Id]);
            var unrelated = Exported(store, other.Id); var unfiled = Exported(store, null);
            // A pending retry has both committed and in-progress artifacts to preserve.
            var active = Exported(store, grandchild.Id); store.Fail(active.Id, "retry"); store.Retry([active.Id]);
            activeId = active.Id; nestedId = nested.Id;
            Assert.True(store.TryStart(active.Id)); store.SaveTranscript(active.Id, "pending transcript");
            var work = store.WorkDirectory(active.Id); File.WriteAllText(Path.Combine(work, "canary"), "keep");
            before = store.Jobs.ToArray();
            foreach (var job in before.Where(j => j.ExportPath != null)) archives.Add(job.ExportPath!, File.ReadAllBytes(job.ExportPath!));
            if (operation == "rename") store.RenameFolder(parent.Id, "Renamed");
            else if (operation == "move") store.MoveFolder(parent.Id, other.Id);
            else store.DeleteFolder(parent.Id);
            var affected = new[] { parent.Id, child.Id, grandchild.Id };
            foreach (var job in before)
            {
                var changed = job.FolderId != null && affected.Contains(job.FolderId);
                Assert.Equal(changed ? job with { FolderId = operation == "delete" ? null : job.FolderId, ExportPath = null } : job, store.Get(job.Id));
                if (job.State == JobState.Purged) continue;
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.AudioPath(job.Id)));
                Assert.Equal("transcript", store.ReadTranscript(job.Id)); Assert.Equal("notes", store.ReadNotes(job.Id));
            }
            Assert.Equal("pending transcript", store.ReadTranscriptForProcessing(active.Id));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(work, "canary")));
            // Finishing an already active job must retain the changed filing.
            store.Complete(active.Id, "fresh notes");
            Assert.Equal(operation == "delete" ? null : grandchild.Id, store.Get(active.Id).FolderId);
            freshExport = store.Export(nested.Id);
            var expectedDirectory = operation switch
            {
                "rename" => Path.Combine(Output, "Renamed", "Child", "Grandchild"),
                "move" => Path.Combine(Output, "Other", "Parent", "Child", "Grandchild"),
                _ => Output
            };
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(freshExport)); Assert.NotEqual(nested.ExportPath, freshExport);
        }
        using var reopened = Open();
        if (operation == "delete") Assert.Equal(new[] { other }, reopened.Folders);
        else
        {
            Assert.Equal(parent with { Name = operation == "rename" ? "Renamed" : parent.Name, ParentId = operation == "move" ? other.Id : null }, reopened.Folders.Single(f => f.Id == parent.Id));
            Assert.Contains(child, reopened.Folders); Assert.Contains(grandchild, reopened.Folders);
        }
        foreach (var job in before)
        {
            var affected = job.FolderId == parent.Id || job.FolderId == child.Id || job.FolderId == grandchild.Id;
            Assert.Equal(affected && operation == "delete" ? null : job.FolderId, reopened.Get(job.Id).FolderId);
            Assert.Equal(job.Trashed, reopened.Get(job.Id).Trashed);
            Assert.Equal(job.Id == nestedId ? freshExport : affected ? null : job.ExportPath, reopened.Get(job.Id).ExportPath);
            if (job.State == JobState.Purged) continue;
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(reopened.AudioPath(job.Id)));
            Assert.Equal(job.Id == activeId ? "pending transcript" : "transcript", reopened.ReadTranscript(job.Id));
            Assert.Equal(job.Id == activeId ? "fresh notes" : "notes", reopened.ReadNotes(job.Id));
        }
        Assert.Equal(freshExport, reopened.Export(before.Single(j => j.FolderId == grandchild.Id && j.State == JobState.Completed).Id));
        foreach (var archive in archives) Assert.Equal(archive.Value, File.ReadAllBytes(archive.Key));
    }
    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("move")]
    [InlineData("delete")]
    public void FailedSaveRollsBackFoldersAndJobReceiptsAndAllowsRetry(string operation)
    {
        LibraryFolder parent, child, target; RecordingJob[] jobs; LibraryFolder[] folders;
        using (var store = Open())
        {
            store.UpdateSettings(new() { OutputDirectory = Output });
            parent = store.CreateFolder("Parent", null); child = store.CreateFolder("Child", parent.Id); target = store.CreateFolder("Target", null);
            Exported(store, parent.Id); var trash = Exported(store, child.Id); store.Trash([trash.Id]);
            jobs = store.Jobs.ToArray(); folders = store.Folders.ToArray();
            void Mutate()
            {
                if (operation == "create") store.CreateFolder("New", parent.Id);
                else if (operation == "rename") store.RenameFolder(parent.Id, "Renamed");
                else if (operation == "move") store.MoveFolder(parent.Id, target.Id);
                else store.DeleteFolder(parent.Id);
            }
            var library = Path.Combine(State, "library.json"); var backup = Path.Combine(root, "saved-library.json");
            File.Move(library, backup); Directory.CreateDirectory(library);
            try
            {
                var error = Record.Exception(Mutate);
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Save unexpectedly succeeded.");
                Assert.Equal(folders, store.Folders); Assert.Equal(jobs, store.Jobs);
                foreach (var job in jobs) Assert.True(File.Exists(job.ExportPath));
            }
            finally { Directory.Delete(library); File.Move(backup, library); }
            // Retry on the same instance to catch incomplete in-memory rollback.
            Mutate();
        }
        using var saved = Open();
        if (operation == "create") Assert.Contains(saved.Folders, f => f.Name == "New");
        else if (operation == "rename") Assert.Equal("Renamed", saved.Folders.Single(f => f.Id == parent.Id).Name);
        else if (operation == "move") Assert.Equal(target.Id, saved.Folders.Single(f => f.Id == parent.Id).ParentId);
        else Assert.Equal(new[] { target }, saved.Folders);
    }
    [Fact]
    public void CollisionsUnknownIdsAndCyclesLeaveStateUnchangedAndSelfEditsAreAllowed()
    {
        using var store = Open(); store.UpdateSettings(new() { OutputDirectory = Output });
        var parent = store.CreateFolder("Parent", null); var child = store.CreateFolder("Child", parent.Id);
        store.CreateFolder("Sibling", parent.Id); var target = store.CreateFolder("Target", null); store.CreateFolder("CHILD", target.Id);
        var job = Exported(store, child.Id); var folders = store.Folders.ToArray(); var persisted = File.ReadAllBytes(Path.Combine(State, "library.json"));
        Assert.Throws<IOException>(() => store.RenameFolder(child.Id, "sIBLING"));
        Assert.Throws<IOException>(() => store.MoveFolder(child.Id, target.Id));
        Assert.Throws<IOException>(() => store.CreateFolder("cHILD", parent.Id));
        Assert.Throws<IOException>(() => store.RenameFolder(parent.Id, "tARGET"));
        Assert.Throws<InvalidDataException>(() => store.MoveFolder(parent.Id, child.Id));
        Assert.Throws<InvalidDataException>(() => store.MoveFolder(child.Id, child.Id));
        foreach (var id in new[] { Guid.NewGuid().ToString(), "unknown", null! })
        {
            Assert.Throws<InvalidDataException>(() => store.RenameFolder(id, "Name"));
            Assert.Throws<InvalidDataException>(() => store.DeleteFolder(id));
            Assert.Throws<InvalidDataException>(() => store.MoveFolder(id, null));
        }
        Assert.Throws<InvalidDataException>(() => store.MoveFolder(child.Id, Guid.NewGuid().ToString()));
        store.RenameFolder(child.Id, "Child"); store.MoveFolder(child.Id, parent.Id);
        Assert.Equal(folders, store.Folders); Assert.Equal(job, store.Get(job.Id)); Assert.Equal(persisted, File.ReadAllBytes(Path.Combine(State, "library.json")));
        store.RenameFolder(child.Id, "cHILD"); Assert.Equal("cHILD", store.Folders.Single(f => f.Id == child.Id).Name); Assert.Null(store.Get(job.Id).ExportPath);
        store.RenameFolder(child.Id, "Target"); // The same name in a different parent is valid.
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("bad/name")]
    public void InvalidNamesDoNotMutateFolders(string name)
    {
        using var store = Open(); var folder = store.CreateFolder("Folder", null);
        Assert.Throws<InvalidDataException>(() => store.RenameFolder(folder.Id, name));
        Assert.Throws<InvalidDataException>(() => store.CreateFolder(name, null));
        Assert.Equal(new[] { folder }, store.Folders);
    }
}
