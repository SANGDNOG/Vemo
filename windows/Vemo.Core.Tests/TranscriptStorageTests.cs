using System.IO.Compression;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class TranscriptStorageTests : IDisposable
{
    readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-timing-" + Guid.NewGuid());
    string State => Path.Combine(root, "state");
    string Audio { get { Directory.CreateDirectory(root); var path = Path.Combine(root, "source.wav"); File.WriteAllBytes(path, [1, 2, 3]); return path; } }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    static readonly TranscriptSegment[] Segments = [new(1, 2, "첫 문장."), new(3, 5, " 다음 문장.")];
    const string Text = "첫 문장. 다음 문장.";
    [Fact] public void TimingReopensSurvivesNotesOnlyRetryAndNeverAddsZipMember()
    {
        string id; using (var store = new LibraryStore(State))
        {
            store.UpdateSettings(new() { OutputDirectory = Path.Combine(root, "exports") }); var job = store.Import(Audio); id = job.Id;
            store.TryStart(id); store.SaveTranscript(id, Text, Segments); store.Complete(id, "notes"); store.Fail(id, "retry fixture"); store.Retry([id]);
        }
        using var reopened = new LibraryStore(State); Assert.Equal(2, reopened.ReadTranscriptDocument(id).Segments.Count);
        reopened.TryStart(id); reopened.SaveTranscript(id, Text, Segments); reopened.Complete(id, "new notes");
        Assert.Equal(2, reopened.ReadTranscriptDocument(id).Segments.Count); using var archive = ZipFile.OpenRead(reopened.Export(id)); Assert.Equal(3, archive.Entries.Count);
    }
    [Fact] public void InvalidTimingCannotReplaceGoodTranscriptAndPlainReplacementDropsSidecar()
    {
        using var store = new LibraryStore(State); var job = store.Import(Audio); store.TryStart(job.Id); store.SaveTranscript(job.Id, Text, Segments); store.Complete(job.Id, "notes");
        Assert.Throws<InvalidDataException>(() => store.SaveTranscript(job.Id, "changed", Segments)); Assert.Equal(Text, store.ReadTranscript(job.Id)); Assert.Equal(2, store.ReadTranscriptDocument(job.Id).Segments.Count);
        store.SaveTranscript(job.Id, "plain replacement"); Assert.Empty(store.ReadTranscriptDocument(job.Id).Segments);
    }
    [Fact] public void TitleDayUpdateIsAtomicPreservesIsoAndUnknownDateDoesNotInventTime()
    {
        using var store = new LibraryStore(State); var job = store.Import(Audio, "old", "2026-10-18T00:30:00+14:00");
        store.UpdateTitleAndDate(job.Id, "new — 2026-10-18", "2026-10-19"); var saved = store.Get(job.Id);
        Assert.Equal("new", saved.Title); Assert.Equal(job.RecordedAt, saved.RecordedAt); Assert.Equal("new — 2026-10-19", RecordingTitles.Display(saved));
        Assert.Throws<InvalidDataException>(() => store.UpdateTitleAndDate(job.Id, "bad", "2026-02-30")); Assert.Equal(saved, store.Get(job.Id));
        var unknown = store.Import(Audio, "unknown"); store.UpdateTitleAndDate(unknown.Id, "known day", "2026-10-17"); Assert.Null(store.Get(unknown.Id).RecordedAt); Assert.Equal("2026-10-17", store.Get(unknown.Id).RecordingDate);
        store.TryStart(job.Id); store.SaveTranscript(job.Id, Text); store.Complete(job.Id, "notes"); store.UpdateSettings(new() { OutputDirectory = Path.Combine(root, "exports") });
        Assert.Equal("new - 2026-10-19.zip", Path.GetFileName(store.Export(job.Id)));
    }
    [Fact] public void MobileDateCannotBeOverriddenByDayOnlyEditing()
    {
        using var store = new LibraryStore(State); var audio = Audio; var day = "2026-10-18T00:30:00Z";
        var mobile = new MobileOrigin(Path.Combine(root, "sync"), Guid.NewGuid().ToString(), SafeFiles.Hash(audio), store.ProcessorId, day, "phone");
        var job = store.Import(audio, "phone", day, mobile); store.UpdateTitleAndDate(job.Id, "edited", "2026-10-18");
        Assert.Throws<InvalidOperationException>(() => store.UpdateTitleAndDate(job.Id, "other", "2026-10-19")); Assert.Equal("edited", store.Get(job.Id).Title); Assert.Equal(day, store.Get(job.Id).RecordedAt);
    }
}
