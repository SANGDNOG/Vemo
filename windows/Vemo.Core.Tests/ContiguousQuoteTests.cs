using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class ContiguousQuoteTests
{
    static string Json(string quote, int[] refs) => new NotesDocument([
        new("주제", "설명", [quote], [quote], refs)
    ]).ToJson();

    [Fact]
    public void ThreeCitedLinesRestoreExactSpanAndRoundTrip()
    {
        string[] sources = ["앞말 조건은\t20 이상이면  ", " 실행하지\r\n않는다. ", " 예: 21. 뒷말"];
        var json = Json("조건은20이상이면실행하지않는다.예:21.", [3, 1, 2, 2]);
        var notes = NotesDocument.Parse(json, sources);
        const string expected = "조건은\t20 이상이면  \n 실행하지\r\n않는다. \n 예: 21.";
        var topic = Assert.Single(notes.Topics);
        Assert.Equal(expected, Assert.Single(topic.Conditions));
        Assert.Equal(expected, Assert.Single(topic.Examples));
        Assert.Equal(new[] { 3, 1, 2, 2 }, topic.Refs);
        Assert.Equal(notes.ToJson(), NotesDocument.Parse(notes.ToJson(), sources).ToJson());
    }

    [Theory]
    [InlineData("조건은20이상이면실행하지않는다.예:21.", new[] { 1, 2 })] // Uncited tail.
    [InlineData("조건은20이상이면실행하지않는다.예:21.", new[] { 4 })] // Wrong references.
    [InlineData("조건은20이상이면예:21.", new[] { 1, 3 })] // Uncited gap.
    [InlineData("조건은20이상이면예:21.", new[] { 1, 2, 3 })] // Cited but skipped content.
    [InlineData("예:21.실행하지않는다.조건은20이상이면", new[] { 3, 2, 1 })]
    [InlineData("조건은30이상이면실행하지않는다.예:21.", new[] { 1, 2, 3 })]
    [InlineData("조건은20이상이면실행한다.예:21.", new[] { 1, 2, 3 })]
    [InlineData("조건은20이상이면실행하지않는다!예:21.", new[] { 1, 2, 3 })]
    [InlineData("조건은20이상이면실행하지않는다.예:21.", new[] { 1, 3, 4 })] // Exact text exists only outside refs.
    public void RejectsUncitedSkippedReorderedOrChangedText(string quote, int[] refs)
    {
        string[] sources = ["조건은 20 이상이면", "실행하지 않는다.", "예: 21.", "무관한 문장"];
        Assert.Throws<InvalidDataException>(() => NotesDocument.Parse(Json(quote, refs), sources));
    }

    [Fact]
    public void RepeatedContentIsNotDeduplicatedAndOverlappingPrefixCanMatch()
    {
        string[] sources = ["abab", "abac", "abac"];
        var notes = NotesDocument.Parse(Json("ababacabac", [1, 2, 3]), sources);
        Assert.Equal("ab\nabac\nabac", Assert.Single(notes.Topics).Conditions[0]);
        Assert.Throws<InvalidDataException>(() => NotesDocument.Parse(Json("ababac", [1, 3]), sources));
    }

    [Fact]
    public void SingleSourceAndLaterCitedRunStillMatch()
    {
        string[] sources = ["unrelated", "uncited", "20\t이상", "실행하지 않는다."];
        var notes = NotesDocument.Parse(Json("20이상실행하지않는다.", [1, 4, 3]), sources);
        Assert.Equal("20\t이상\n실행하지 않는다.", notes.Topics[0].Conditions[0]);
        Assert.Equal("실행하지 않는다.", NotesDocument.Parse(Json("실행하지않는다.", [4]), sources).Topics[0].Examples[0]);
        Assert.Throws<InvalidDataException>(() => NotesDocument.Parse(Json(" \t\n", [4]), sources));
    }

    [Fact]
    public async Task ChunkOffsetsPreserveContiguousQuotesAndGlobalValidation()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-contiguous-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string[] sources = [new string('한', (LongNotes.SourceByteLimit - 20) / 3), "조건은 20 이상이면", "실행하지 않는다.", "예: 21."];
            var chunks = LongNotes.Plan(sources);
            Assert.Equal(2, chunks.Count);
            Assert.Equal(1, chunks[1].Start);
            var context = new InferenceContext(new RecordingJob { Id = "contiguous", Title = "fixture", AudioSha256 = "unused" }, "unused", root);
            int calls = 0;
            var json = await LongNotes.GenerateAsync(context, sources, "fixture", (_, _) =>
                Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { schema_version = 1, topics = new[] { new { heading = "주제", summary = "설명", condition_refs = new[] { ++calls == 1 ? new[] { 1 } : new[] { 1, 2, 3 } }, example_refs = Array.Empty<int[]>(), refs = calls == 1 ? new[] { 1 } : new[] { 1, 2, 3 } } } })), CancellationToken.None);
            Assert.Equal(2, calls);
            var notes = NotesDocument.Parse(json, sources);
            Assert.Equal(new[] { 2, 3, 4 }, notes.Topics[1].Refs);
            Assert.Equal("조건은 20 이상이면\n실행하지 않는다.\n예: 21.", notes.Topics[1].Conditions[0]);
            Assert.Equal(notes.ToJson(), NotesDocument.Parse(notes.ToJson(), sources).ToJson());
            var wrongRefs = notes.Topics[1] with { Refs = [1, 2, 3] };
            Assert.Throws<InvalidDataException>(() => NotesDocument.Parse(new NotesDocument([wrongRefs]).ToJson(), sources));
        }
        finally { Directory.Delete(root, true); }
    }
}
