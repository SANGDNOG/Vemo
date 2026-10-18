using System.IO.Compression;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class RecordingMetadataIndependentTests
{
    [Theory]
    [InlineData("2026-10-18T00:05:00+14:00", "2026-10-18")]
    [InlineData("2026-10-18T23:55:00-12:00", "2026-10-18")]
    [InlineData("2026-10-18T10:00:00Z", "2026-10-18")]
    public void Offset_date_keeps_written_calendar_day_and_plain_export(string recordedAt, string day)
    {
        using var f = new Fixture();
        string id;
        using (var store = new LibraryStore(f.Library))
        {
            store.UpdateSettings(new() { OutputDirectory = f.Output });
            id = store.Import(f.Audio, "Offset fixture", recordedAt).Id;
            Assert.True(store.TryStart(id)); store.SaveTranscript(id, "First exact sentence.\nSecond exact sentence."); store.Complete(id, "# Fixture notes");
            Assert.Equal(recordedAt, store.Get(id).RecordedAt);
            var path = store.Export(id); Assert.Contains(day, Path.GetFileName(path));
            using var archive = ZipFile.OpenRead(path);
            using var text = new StreamReader(archive.GetEntry("transcript.txt")!.Open());
            Assert.Equal("First exact sentence.\nSecond exact sentence.", text.ReadToEnd());
            Assert.Equal(new[] { "notes.md", "original.wav", "transcript.txt" }, archive.Entries.Select(x => x.FullName).Order());
        }
        using var reopened = new LibraryStore(f.Library);
        Assert.Equal(recordedAt, reopened.Get(id).RecordedAt);
    }

    [Fact]
    public void Unknown_date_remains_unknown_despite_old_source_mtime_and_reopen()
    {
        using var f = new Fixture();
        File.SetLastWriteTimeUtc(f.Audio, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        string id;
        using (var store = new LibraryStore(f.Library))
        {
            store.UpdateSettings(new() { OutputDirectory = f.Output });
            id = store.Import(f.Audio, "Unknown date").Id;
            Assert.Null(store.Get(id).RecordedAt);
            Assert.True(store.TryStart(id)); store.SaveTranscript(id, "Exact raw text"); store.Complete(id, "# Notes");
            Assert.Contains("date-unknown", Path.GetFileName(store.Export(id)));
        }
        using var reopened = new LibraryStore(f.Library); Assert.Null(reopened.Get(id).RecordedAt);
    }

    [Theory]
    [InlineData("2026-10-18T00:00:00")]
    [InlineData("2026-02-30T10:00:00+09:00")]
    public void Invalid_or_timezone_free_timestamp_cannot_replace_original_metadata(string candidate)
    {
        using var f = new Fixture(); using var store = new LibraryStore(f.Library);
        const string original = "2026-10-18T00:05:00+14:00";
        var job = store.Import(f.Audio, "Date fixture", original);
        Assert.Throws<InvalidDataException>(() => store.SetRecordedAt(job.Id, candidate));
        Assert.Equal(original, store.Get(job.Id).RecordedAt);
    }

    [Fact]
    public void Separate_day_round_trips_without_inventing_time_and_invalid_day_is_atomic_with_title()
    {
        using var f = new Fixture(); string id;
        using (var store = new LibraryStore(f.Library))
        {
            store.UpdateSettings(new() { OutputDirectory = f.Output });
            id = store.Import(f.Audio, "Day fixture").Id;
            store.UpdateTitleAndDate(id, "Renamed — 2026-10-18", "2026-10-18");
            Assert.Equal("Renamed", store.Get(id).Title); Assert.Null(store.Get(id).RecordedAt);
            Assert.Equal("2026-10-18", store.Get(id).RecordingDate);
            Assert.Equal("Renamed — 2026-10-18", RecordingTitles.Display(store.Get(id)));
            Assert.Throws<InvalidDataException>(() => store.UpdateTitleAndDate(id, "Must not save", "2026-02-30"));
            Assert.Equal("Renamed", store.Get(id).Title); Assert.Equal("2026-10-18", store.Get(id).RecordingDate);
            Assert.True(store.TryStart(id)); store.SaveTranscript(id, "Exact plain text."); store.Complete(id, "# Notes");
            var archive = Path.GetFileName(store.Export(id));
            Assert.Equal("Renamed - 2026-10-18.zip", archive);
        }
        using var reopened = new LibraryStore(f.Library);
        Assert.Null(reopened.Get(id).RecordedAt); Assert.Equal("2026-10-18", reopened.Get(id).RecordingDate);
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "vemo-metadata-independent-" + Guid.NewGuid()).Replace("/var/", "/private/var/");
        public string Library => Path.Combine(root, "library");
        public string Output => Path.Combine(root, "export");
        public string Audio => Path.Combine(root, "source.wav");
        public Fixture() { Directory.CreateDirectory(root); File.WriteAllBytes(Audio, [82, 73, 70, 70, 1]); }
        public void Dispose() => Directory.Delete(root, true);
    }
}
