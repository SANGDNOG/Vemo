using System.Text.Json;
using System.Text.Json.Nodes;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class StructuredNotesOutputTests : IDisposable
{
    readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-wire-" + Guid.NewGuid());
    public StructuredNotesOutputTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    InferenceContext Context => new(new RecordingJob { Id = "wire", Title = "wire", AudioSha256 = "unused" }, "unused", directory);
    static string Wire(int[][]? conditions = null, int[][]? examples = null, int[]? refs = null) =>
        JsonSerializer.Serialize(new { schema_version = 1, topics = new[] { new {
            heading = "조건과 예시", summary = "원문에 명시된 조건이며 출시일은 미정입니다.",
            condition_refs = conditions ?? [[1, 2]], example_refs = examples ?? [[3]], refs = refs ?? [3, 2, 1]
        } } });
    static readonly string[] Sources = [" 20 이상이면 실행하지 않는다. ", "출시일은 미정이다!", "예: 21, 아니다. "];

    [Fact] public async Task WireMaterializesExactWholeLinesAndCachesOnlyCanonicalNotes()
    {
        string wire = Wire();
        var result = await LongNotes.GenerateAsync(Context, Sources, "model", (_, _) => Task.FromResult(wire), CancellationToken.None);
        // Parse by count here to inspect exact whitespace, before canonical source validation normalizes it.
        var topic = Assert.Single(NotesDocument.Parse(result, Sources.Length).Topics);
        Assert.Equal(Sources[0] + "\n" + Sources[1], Assert.Single(topic.Conditions));
        Assert.Equal(Sources[2], Assert.Single(topic.Examples));
        NotesDocument.Parse(result, Sources);
        Assert.DoesNotContain("condition_refs", result);
        var checkpoint = Assert.Single(Directory.GetFiles(Path.Combine(directory, "notes-checkpoints")));
        Assert.Equal(result, File.ReadAllText(checkpoint));
        Assert.Equal(result, await LongNotes.GenerateAsync(Context, Sources, "model", (_, _) => throw new Exception("must reuse canonical checkpoint"), CancellationToken.None));
        // A wire response cannot be accepted as a canonical checkpoint.
        File.WriteAllText(checkpoint, wire);
        int calls = 0;
        Assert.Equal(result, await LongNotes.GenerateAsync(Context, Sources, "model", (_, _) => { calls++; return Task.FromResult(wire); }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact] public async Task MultiLineSelectionsRemapToGlobalReferencesAcrossChunks()
    {
        var sources = Enumerable.Range(1, 10).Select(i => $"{i}: " + new string('한', 500) + " 아니면 안 된다.").ToArray();
        var chunks = LongNotes.Plan(sources); Assert.True(chunks.Count > 1);
        int calls = 0;
        var result = await LongNotes.GenerateAsync(Context, sources, "model", (_, _) =>
        {
            var chunk = chunks[calls++];
            var indices = Enumerable.Range(1, chunk.Sources.Count).ToArray();
            return Task.FromResult(Wire([indices], [], indices));
        }, CancellationToken.None);
        var topics = NotesDocument.Parse(result, sources.Length).Topics;
        Assert.Equal(chunks.Count, calls);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(Enumerable.Range(chunks[i].Start + 1, chunks[i].Sources.Count), topics[i].Refs);
            Assert.Equal(string.Join("\n", chunks[i].Sources), Assert.Single(topics[i].Conditions));
        }
        NotesDocument.Parse(result, sources);
    }

    [Theory]
    [InlineData("empty-group")][InlineData("zero")][InlineData("negative")][InlineData("outside")]
    [InlineData("reverse")][InlineData("gap")][InlineData("repeat")][InlineData("uncited")]
    [InlineData("string")][InlineData("float")][InlineData("quote-string")][InlineData("null")]
    [InlineData("missing-field")][InlineData("unknown-field")][InlineData("old-quotes")]
    [InlineData("schema")][InlineData("duplicate")][InlineData("empty-refs")]
    [InlineData("empty-topics")][InlineData("topics")][InlineData("heading")][InlineData("summary")]
    [InlineData("blank")][InlineData("groups")][InlineData("indices")][InlineData("refs")]
    public async Task InvalidWireIsRejectedWithoutRepairAndNeverCheckpointed(string failure)
    {
        var root = JsonNode.Parse(Wire())!.AsObject();
        var topics = root["topics"]!.AsArray(); var topic = topics[0]!.AsObject();
        switch (failure)
        {
            case "empty-group": topic["condition_refs"] = JsonNode.Parse("[[]]"); break;
            case "zero": topic["condition_refs"] = JsonNode.Parse("[[0]]"); break;
            case "negative": topic["example_refs"] = JsonNode.Parse("[[-1]]"); break;
            case "outside": topic["refs"] = JsonNode.Parse("[4]"); break;
            case "reverse": topic["condition_refs"] = JsonNode.Parse("[[2,1]]"); break;
            case "gap": topic["condition_refs"] = JsonNode.Parse("[[1,3]]"); break;
            case "repeat": topic["condition_refs"] = JsonNode.Parse("[[1,1]]"); break;
            case "uncited": topic["refs"] = JsonNode.Parse("[1,3]"); break;
            case "string": topic["condition_refs"] = JsonNode.Parse("[[\"1\"]]"); break;
            case "float": topic["condition_refs"] = JsonNode.Parse("[[1.0]]"); break;
            case "quote-string": topic["example_refs"] = JsonNode.Parse("[\"예: 31!\"]"); break;
            case "null": topic["condition_refs"] = null; break;
            case "missing-field": topic.Remove("example_refs"); break;
            case "unknown-field": topic["invented"] = "arbitrary"; break;
            case "old-quotes": topic["conditions"] = JsonNode.Parse("[\"20 이상이면 실행한다.\"]"); break;
            case "schema": root["schema_version"] = 2; break;
            case "duplicate": break;
            case "empty-refs": topic["refs"] = new JsonArray(); break;
            case "empty-topics": topics.Clear(); break;
            case "topics": for (int i = 1; i < 9; i++) topics.Add(topic.DeepClone()); break;
            case "heading": topic["heading"] = new string('가', 101); break;
            case "summary": topic["summary"] = new string('가', 701); break;
            case "blank": topic["summary"] = " \t"; break;
            case "groups": topic["example_refs"] = JsonNode.Parse("[[1],[1],[1],[1]]"); break;
            case "indices": topic["condition_refs"] = JsonSerializer.SerializeToNode(new[] { Enumerable.Range(1, 9).ToArray() }); break;
            case "refs": topic["refs"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(1, 65)); break;
        }
        var bad = root.ToJsonString();
        if (failure == "duplicate") bad = bad.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1");
        int calls = 0;
        // Nine valid source indices ensure the group-length test does not merely fail a source bound.
        var sources = failure == "indices" ? Enumerable.Repeat("원문", 9).ToArray() : Sources;
        if (failure == "indices")
        {
            root["topics"]![0]!["refs"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 9));
            bad = root.ToJsonString();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => LongNotes.GenerateAsync(Context, sources, "model", (_, _) =>
        { calls++; return Task.FromResult(bad); }, CancellationToken.None));
        Assert.Equal(LongNotes.MaxGenerationAttempts, calls);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "notes-checkpoints")));
    }

    [Fact] public void MaximumOutputBoundsAreAcceptedAndSchemaDescribesWireOnly()
    {
        string[] sources = Enumerable.Range(1, 64).Select(i => $"원문 {i} 아니다!").ToArray();
        var root = JsonNode.Parse(Wire([[1,2,3,4,5,6,7,8],[9],[10]], [[11],[12],[13]], Enumerable.Range(1, 64).ToArray()))!;
        var topic = root["topics"]![0]!;
        topic["heading"] = new string('가', 100); topic["summary"] = new string('나', 700);
        var topics = root["topics"]!.AsArray();
        for (int i = 1; i < 8; i++) topics.Add(topic.DeepClone());
        var notes = LongNotes.ParseGenerated(root.ToJsonString(), sources);
        Assert.Equal(8, notes.Topics.Count);
        Assert.Equal(string.Join("\n", sources.Take(8)), notes.Topics[0].Conditions[0]);
        using var schema = JsonDocument.Parse(LongNotes.GeneratedJsonSchema);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        var item = schema.RootElement.GetProperty("properties").GetProperty("topics").GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(8, schema.RootElement.GetProperty("properties").GetProperty("topics").GetProperty("maxItems").GetInt32());
        Assert.Contains("condition_refs", item.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("\"conditions\"", LongNotes.GeneratedJsonSchema);
    }
}
