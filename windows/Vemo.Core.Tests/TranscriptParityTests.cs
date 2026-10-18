using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class TranscriptParityTests
{
    [Fact] public void RealWhisperOffsetsAreMillisecondsAndWhitespaceAlignmentIsExact()
    {
        var text = "첫 문장입니다. 다음 문장입니다.";
        var segments = WhisperTranscript.Parse("{\"transcription\":[{\"offsets\":{\"from\":0,\"to\":5460},\"text\":\" 첫 문장입니다.\"},{\"offsets\":{\"from\":5460,\"to\":8250},\"text\":\" 다음 문장입니다.\"}]}", text);
        Assert.Equal(5.46, segments[0].EndSeconds); Assert.Equal(8.25, segments[1].EndSeconds);
        var rows = TranscriptDisplay.Rows(new(text, segments)); Assert.Equal(text, string.Concat(rows.Select(x => x.Text))); Assert.Equal(2, rows.Count); Assert.Equal(5.46, rows[1].StartSeconds);
    }
    [Fact] public void UnalignedOrTextOnlyDocumentsHaveReadableRowsAndNoInventedTiming()
    {
        var text = "같은 문장. 같은 문장.\n\n" + new string('가', 700);
        var rows = TranscriptDisplay.Rows(new(text, [])); Assert.Equal(text, string.Concat(rows.Select(x => x.Text))); Assert.All(rows, r => { Assert.Null(r.StartSeconds); Assert.InRange(r.Text.Length, 1, 320); });
        rows = TranscriptDisplay.Rows(new(text, [new(0, 2, "다른 전사")])); Assert.All(rows, r => Assert.Null(r.StartSeconds));
    }
    [Fact] public void RepeatedSentencesUseOrderedSegmentsAndNeverSubstringSearch()
    {
        var rows = TranscriptDisplay.Rows(new("동일합니다. 동일합니다.", [new(1, 2, "동일합니다."), new(9, 10, "동일합니다.")]));
        Assert.Equal(1, rows[0].StartSeconds); Assert.Equal(9, rows[1].StartSeconds);
        Assert.Throws<InvalidDataException>(() => WhisperTranscript.Parse("{\"transcription\":[{\"offsets\":{\"from\":-1,\"to\":2},\"text\":\"x\"}]}", "x"));
    }
    [Fact] public void CanonicalClusterAcrossChunkOrSegmentBoundaryFallsBackWithoutLosingText()
    {
        var text = new string('a', 319) + "e\u0301 tail";
        var rows = TranscriptDisplay.Rows(new(text, [new(0, 2, text)])); Assert.Equal(text, string.Concat(rows.Select(x => x.Text))); Assert.All(rows, r => Assert.Null(r.StartSeconds));
        rows = TranscriptDisplay.Rows(new("e\u0301", [new(0, 1, "e"), new(1, 2, "\u0301")])); Assert.Equal("e\u0301", string.Concat(rows.Select(x => x.Text))); Assert.All(rows, r => Assert.Null(r.StartSeconds));
    }
    [Theory]
    [InlineData("topic", "2026-10-18", "topic — 2026-10-18")]
    [InlineData("topic — 2025-01-01 — 2026-10-18", "2026-10-18", "topic — 2026-10-18")]
    [InlineData("topic", null, "topic")]
    public void TitlesHaveOneKnownDaySuffixAndUnknownDatesStayUnknown(string title, string? day, string expected)
    {
        var job = new RecordingJob { Id = Guid.NewGuid().ToString(), Title = title, AudioSha256 = "fixture", RecordingDate = day };
        Assert.Equal(expected, RecordingTitles.Display(job));
    }
    [Fact] public void WhisperReportedProgressIsClampedWhileRunningAndUnrelatedNumbersAreIgnored()
    {
        Assert.True(WhisperProgress.TryPercent("whisper_print_progress_callback: progress = 52%", out var percent)); Assert.Equal(52, percent);
        Assert.True(WhisperProgress.TryPercent("whisper_print_progress_callback: progress = 104%", out percent)); Assert.Equal(99, percent);
        Assert.False(WhisperProgress.TryPercent("model context 52% loaded", out _));
    }
}
