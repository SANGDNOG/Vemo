using System.IO.Compression;
using System.Text.Json.Nodes;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class SpeakerStorageTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "vemo-speakers-" + Guid.NewGuid());
    string State => Path.Combine(root, "state");
    const string Text = "첫 문장. 다음 문장.";
    static readonly TranscriptSegment[] Segments = [new(0, 2, "첫 문장."), new(2, 4, " 다음 문장.")];
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    RecordingJob Completed(LibraryStore store)
    {
        Directory.CreateDirectory(root); var audio = Path.Combine(root, "input.wav"); File.WriteAllBytes(audio, [1, 2, 3]);
        store.UpdateSettings(new() { OutputDirectory = Path.Combine(root, "exports") });
        var job = store.Import(audio); store.TryStart(job.Id); store.SaveTranscript(job.Id, Text, Segments); store.Complete(job.Id, "original notes"); return store.Get(job.Id);
    }
    static SpeakerDocument Analyze(RecordingJob job) => SpeakerDocument.Create(job.AudioSha256, new(Text, Segments), [new SpeakerTurn(0, 4, "raw")]);
    static string Generation(SpeakerDocument doc) => JsonNode.Parse(doc.Serialize())!["generation"]!.GetValue<string>();

    [Fact] public void OptionalSidecarReopensRenameDoesNotChangeOriginalsAndZipStaysThreeEntries()
    {
        string id, saved;
        using (var store = new LibraryStore(State))
        {
            var job = Completed(store); id = job.Id; Assert.Null(store.ReadSpeakerDocument(id)); var document = Analyze(job);
            store.SaveSpeakerDocument(id, document);
            store.RenameSpeaker(id, Generation(document), "speaker_1", "  진행자  ");
            saved = store.ReadSpeakerDocument(id)!.Serialize();
            Assert.Equal("진행자", JsonNode.Parse(saved)!["speakers"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(Text, store.ReadTranscript(id)); Assert.Equal("original notes", store.ReadNotes(id));
            using var zip = ZipFile.OpenRead(store.Export(id)); Assert.Equal(3, zip.Entries.Count);
        }
        using var reopened = new LibraryStore(State); Assert.Equal(saved, reopened.ReadSpeakerDocument(id)!.Serialize());
        Assert.Equal(Text, reopened.ReadTranscript(id)); Assert.Equal("original notes", reopened.ReadNotes(id));
    }

    [Fact] public void RejectedStaleReplacementAndBadRenamePreserveCommittedOutput()
    {
        using var store = new LibraryStore(State); var job = Completed(store); var document = Analyze(job); store.SaveSpeakerDocument(job.Id, document);
        var old = store.ReadSpeakerDocument(job.Id)!.Serialize();
        var stale = SpeakerDocument.Create(new string('b', 64), new(Text, Segments), [new SpeakerTurn(0, 4, "raw")]);
        Assert.Throws<InvalidDataException>(() => store.SaveSpeakerDocument(job.Id, stale));
        Assert.Throws<InvalidDataException>(() => store.RenameSpeaker(job.Id, Generation(document), "speaker_1", "bad\nname"));
        Assert.ThrowsAny<Exception>(() => store.RenameSpeaker(job.Id, Guid.NewGuid().ToString(), "speaker_1", "stale"));
        Assert.Equal(old, store.ReadSpeakerDocument(job.Id)!.Serialize());
        Assert.Equal(Text, store.ReadTranscript(job.Id)); Assert.Equal("original notes", store.ReadNotes(job.Id));
    }

    [Fact] public void RerunUsesNewGenerationAndResetsNames()
    {
        using var store = new LibraryStore(State); var job = Completed(store); var first = Analyze(job); store.SaveSpeakerDocument(job.Id, first);
        store.RenameSpeaker(job.Id, Generation(first), "speaker_1", "이전 이름");
        var rerun = Analyze(job); store.SaveSpeakerDocument(job.Id, rerun);
        Assert.NotEqual(Generation(first), Generation(rerun));
        Assert.Equal("화자 1", JsonNode.Parse(store.ReadSpeakerDocument(job.Id)!.Serialize())!["speakers"]![0]!["name"]!.GetValue<string>());
    }

    [Fact] public void ChangedTranscriptRejectsOldAnalysisAndHidesStaleSidecar()
    {
        using var store = new LibraryStore(State); var job = Completed(store); var old = Analyze(job); store.SaveSpeakerDocument(job.Id, old);
        store.SaveTranscript(job.Id, "replacement transcript");
        Assert.Null(store.ReadSpeakerDocument(job.Id));
        store.Complete(job.Id, "original notes");
        Assert.Throws<InvalidDataException>(() => store.SaveSpeakerDocument(job.Id, old));
        Assert.Equal("replacement transcript", store.ReadTranscript(job.Id)); Assert.Equal("original notes", store.ReadNotes(job.Id));
    }

    [Fact] public void NamesUseUnicodeScalarLimitAndPreserveGeneration()
    {
        using var store = new LibraryStore(State); var job = Completed(store); var document = Analyze(job); store.SaveSpeakerDocument(job.Id, document);
        var name = string.Concat(Enumerable.Repeat("😀", 80)); store.RenameSpeaker(job.Id, Generation(document), "speaker_1", name);
        var renamed = store.ReadSpeakerDocument(job.Id)!;
        Assert.Equal(Generation(document), Generation(renamed));
        Assert.Equal(name, JsonNode.Parse(renamed.Serialize())!["speakers"]![0]!["name"]!.GetValue<string>());
        Assert.Throws<InvalidDataException>(() => store.RenameSpeaker(job.Id, Generation(document), "speaker_1", name + "😀"));
    }
}
