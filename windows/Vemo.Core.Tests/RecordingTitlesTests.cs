using System.Text.Json.Nodes;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class RecordingTitlesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "vemo-titles-" + Guid.NewGuid());
    string Library => Path.Combine(root, "library");
    LibraryStore Open() => new(Library);
    string Audio { get { Directory.CreateDirectory(root); var path = Path.Combine(root, "upload.wav"); File.WriteAllBytes(path, [1, 2, 3]); return path; } }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    static readonly NotesDocument Document = NotesDocument.Parse("""
        {"schema_version":1,"topics":[
          {"heading":"Overview","summary":"Intro","conditions":[],"examples":[],"refs":[1]},
          {"heading":"Battery recycling economics","summary":"Main","conditions":[],"examples":[],"refs":[1,2,3]},
          {"heading":"Collection logistics","summary":"Other","conditions":[],"examples":[],"refs":[2,3]}]}
        """, new[] { "First", "Second", "Third" });

    [Fact]
    public async Task Validated_notes_name_new_import_and_explicit_regeneration_uses_saved_notes()
    {
        string id;
        using (var store = Open())
        {
            store.UpdateSettings(new() { AutoExport = false, OutputDirectory = Path.Combine(root, "exports") });
            id = store.Import(Audio, "upload", "2026-10-18T00:05:00+14:00").Id;
            await new JobRunner(store, new Engine()).RunNextAsync();
            Assert.Equal(JobState.Completed, store.Get(id).State);
            Assert.Equal("Battery recycling economics · Collection logistics", store.Get(id).Title);
            Assert.Equal(TitleOwnership.Automatic, store.Get(id).TitleOwnership);
            Assert.Equal(store.Get(id).Title + " — 2026-10-18", RecordingTitles.Display(store.Get(id)));
            var notes = store.ReadNotes(id); var zip = store.Export(id); var archive = File.ReadAllBytes(zip);
            store.UpdateTitleAndDate(id, "My title — 2026-10-18 — 2026-10-18", "2026-10-18");
            Assert.Equal("My title — 2026-10-18", RecordingTitles.Display(store.Get(id)));
            Assert.Equal(notes, store.ReadNotes(id)); Assert.Equal(archive, File.ReadAllBytes(zip));
            Assert.True(store.RegenerateTitle(id));
            Assert.Equal(TitleOwnership.Automatic, store.Get(id).TitleOwnership);
            Assert.Equal("Battery recycling economics · Collection logistics", store.Get(id).Title);
            Assert.Equal(notes, store.ReadNotes(id)); Assert.Equal(archive, File.ReadAllBytes(zip));
        }
        using var reopened = Open(); Assert.Equal(TitleOwnership.Automatic, reopened.Get(id).TitleOwnership);
    }

    [Fact]
    public async Task Manual_edit_during_generation_survives_completion_and_reopen()
    {
        string id;
        using (var store = Open())
        {
            store.UpdateSettings(new() { AutoExport = false }); id = store.Import(Audio).Id;
            var engine = new Engine(wait: true); var running = new JobRunner(store, engine).RunNextAsync();
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.Rename(id, "Manual topic"); engine.Release.TrySetResult(); await running;
            Assert.Equal(JobState.Completed, store.Get(id).State);
            Assert.Equal("Manual topic", store.Get(id).Title);
            Assert.Equal(TitleOwnership.Manual, store.Get(id).TitleOwnership);
        }
        using var reopened = Open(); Assert.Equal("Manual topic", reopened.Get(id).Title);
        Assert.Equal(TitleOwnership.Manual, reopened.Get(id).TitleOwnership);
    }

    [Fact]
    public async Task Legacy_missing_ownership_is_preserved_until_explicit_request()
    {
        string id;
        using (var store = Open()) { store.UpdateSettings(new() { AutoExport = false }); id = store.Import(Audio, "Old custom title").Id; }
        var path = Path.Combine(Library, "library.json"); var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Jobs"]![0]!.AsObject().Remove("TitleOwnership"); File.WriteAllText(path, json.ToJsonString());
        using var reopened = Open(); Assert.Equal(TitleOwnership.Unknown, reopened.Get(id).TitleOwnership);
        await new JobRunner(reopened, new Engine()).RunNextAsync();
        Assert.Equal("Old custom title", reopened.Get(id).Title);
        Assert.True(reopened.RegenerateTitle(id));
        Assert.Equal("Battery recycling economics · Collection logistics", reopened.Get(id).Title);
    }

    [Fact]
    public void Automatic_compare_and_swap_rejects_stale_manual_snapshot_even_same_text()
    {
        using var store = Open(); var imported = store.Import(Audio, "upload");
        store.Rename(imported.Id, imported.Title);
        Assert.False(store.TryApplyAutomaticTitle(imported, Document));
        Assert.Equal(TitleOwnership.Manual, store.Get(imported.Id).TitleOwnership);
        var error = Assert.Throws<InvalidOperationException>(() => store.RegenerateTitle(imported.Id));
        Assert.Contains("노트", error.Message); Assert.Equal("upload", store.Get(imported.Id).Title);
    }

    [Fact]
    public void Date_only_update_keeps_automatic_eligibility_and_revision_blocks_ABA()
    {
        using var store = Open(); var imported = store.Import(Audio, "upload");
        store.UpdateTitleAndDate(imported.Id, "ignored", "2026-10-18", updateTitle: false);
        Assert.Equal(TitleOwnership.Imported, store.Get(imported.Id).TitleOwnership);
        Assert.True(store.TryApplyAutomaticTitle(imported, Document));
        var automatic = store.Get(imported.Id);
        store.Rename(imported.Id, "Temporary manual name"); store.Rename(imported.Id, automatic.Title);
        Assert.True(store.Get(imported.Id).TitleRevision > automatic.TitleRevision);
        Assert.False(store.TryApplyAutomaticTitle(automatic, Document));
        Assert.Equal(TitleOwnership.Manual, store.Get(imported.Id).TitleOwnership);
        Assert.Equal(automatic.Title + " — 2026-10-18", RecordingTitles.Display(store.Get(imported.Id)));
    }

    [Fact]
    public void Explicit_regeneration_rejects_markdown_only_and_invalid_stored_json()
    {
        using var store = Open(); var job = store.Import(Audio, "Preserve me");
        store.SaveTranscript(job.Id, "First"); store.Complete(job.Id, "# Arbitrary markdown\n## Misleading topic");
        Assert.Throws<InvalidOperationException>(() => store.RegenerateTitle(job.Id));
        File.WriteAllText(Path.Combine(store.WorkDirectory(job.Id), "notes.json"), """
            {"schema_version":1,"topics":[{"heading":"Wrong refs","summary":"x","conditions":[],"examples":[],"refs":[99]}]}
            """);
        Assert.Throws<InvalidDataException>(() => store.RegenerateTitle(job.Id));
        Assert.Equal("Preserve me", store.Get(job.Id).Title);
    }

    [Fact]
    public async Task Same_automatic_title_and_ownership_after_regeneration_reject_stale_revision()
    {
        using var store = Open(); store.UpdateSettings(new() { AutoExport = false });
        var id = store.Import(Audio).Id;
        await new JobRunner(store, new Engine()).RunNextAsync();
        var before = store.Get(id);
        store.Rename(id, "Temporary manual title");
        Assert.True(store.RegenerateTitle(id));
        Assert.Equal(before.Title, store.Get(id).Title);
        Assert.Equal(before.TitleOwnership, store.Get(id).TitleOwnership);
        Assert.NotEqual(before.TitleRevision, store.Get(id).TitleRevision);
        Assert.False(store.TryApplyAutomaticTitle(before, Document));
    }

    [Fact]
    public async Task Date_only_edit_during_generation_survives_completion_and_reopen()
    {
        string id;
        using (var store = Open())
        {
            store.UpdateSettings(new() { AutoExport = false }); id = store.Import(Audio).Id;
            var engine = new Engine(wait: true); var running = new JobRunner(store, engine).RunNextAsync();
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.UpdateTitleAndDate(id, store.Get(id).Title, "2026-10-12", updateTitle: false);
            engine.Release.TrySetResult(); await running;
            Assert.Equal(JobState.Completed, store.Get(id).State);
            Assert.Equal(TitleOwnership.Automatic, store.Get(id).TitleOwnership);
            Assert.EndsWith(" — 2026-10-12", RecordingTitles.Display(store.Get(id)));
        }
        using var reopened = Open();
        Assert.Equal("2026-10-12", reopened.Get(id).RecordingDate);
        Assert.Equal(TitleOwnership.Automatic, reopened.Get(id).TitleOwnership);
    }

    [Fact]
    public async Task Invalid_notes_never_change_import_title()
    {
        using var store = Open(); var job = store.Import(Audio, "upload");
        await new JobRunner(store, new Engine(invalid: true)).RunNextAsync();
        Assert.Equal(JobState.Failed, store.Get(job.Id).State); Assert.Equal("upload", store.Get(job.Id).Title);
    }

    [Fact]
    public void Generated_title_is_bounded_safe_and_ignores_generic_or_duplicate_headings()
    {
        var document = new NotesDocument(new[] {
            new NoteTopic("Overview", "intro", [], [], [1]),
            new NoteTopic("CON", "reserved", [], [], [1]),
            new NoteTopic("  Battery / recycling:\n" + new string('x', 200) + "😀", "topic", [], [], [1,2]),
            new NoteTopic("Overview", "duplicate", [], [], [1]) });
        var title = RecordingTitles.FromNotes(document)!;
        Assert.InRange(title.Length, 1, 80); SafeFiles.Name(title);
        Assert.StartsWith("Battery recycling", title); Assert.DoesNotContain("Overview", title);
        Assert.Null(RecordingTitles.FromNotes(new NotesDocument([new("Overview", "intro", [], [], [1])])));
    }

    sealed class Engine(bool wait = false, bool invalid = false) : IInferenceEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct) => Task.FromResult("First\nSecond\nThird");
        public async Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
        { Started.TrySetResult(); if (wait) await Release.Task.WaitAsync(ct); return invalid ? "{}" : Document.ToJson(); }
    }
}
