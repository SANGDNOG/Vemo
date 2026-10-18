using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class NotesGrammarTests
{
    static string Rule(string grammar, string name) => Assert.Single(grammar.Split('\n')
        .Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith(name + " ::= ", StringComparison.Ordinal)));

    [Fact] public void GrammarUsesLiteralPrefixAndBoundedJsonStringsAndCollections()
    {
        var grammar = NotesGrammar.Create(10);
        Assert.Equal("root ::= \"<|im_start|>assistant\\n<think>\\n\\n</think>\\n\\n\" object", Rule(grammar, "root"));
        Assert.Contains("\"\\\"schema_version\\\"\"", Rule(grammar, "object"));
        Assert.Contains("topic (ws \",\" ws topic){0,7}", Rule(grammar, "object"));
        Assert.Equal("heading ::= \"\\\"\" char{1,100} \"\\\"\"", Rule(grammar, "heading"));
        Assert.Equal("summary ::= \"\\\"\" char{1,700} \"\\\"\"", Rule(grammar, "summary"));
        Assert.Equal("""char ::= [^"\\\x00-\x1f] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4})""", Rule(grammar, "char"));
        Assert.Equal("groups ::= \"[\" ws (group (ws \",\" ws group){0,2})? ws \"]\"", Rule(grammar, "groups"));
        Assert.Equal("refs ::= \"[\" ws index (ws \",\" ws index){0,63} ws \"]\"", Rule(grammar, "refs"));
    }

    [Fact] public void SourceBoundGroupsOnlyExtendConsecutivelyUpToEightOrSourceEnd()
    {
        var grammar = NotesGrammar.Create(10);
        Assert.Equal("index ::= \"1\" | \"2\" | \"3\" | \"4\" | \"5\" | \"6\" | \"7\" | \"8\" | \"9\" | \"10\"", Rule(grammar, "index"));
        Assert.Equal("group-1 ::= \"[\" ws \"1\" (ws \",\" ws \"2\" (ws \",\" ws \"3\" (ws \",\" ws \"4\" (ws \",\" ws \"5\" (ws \",\" ws \"6\" (ws \",\" ws \"7\" (ws \",\" ws \"8\")?)?)?)?)?)?)? ws \"]\"", Rule(grammar, "group-1"));
        Assert.Equal("group-9 ::= \"[\" ws \"9\" (ws \",\" ws \"10\")? ws \"]\"", Rule(grammar, "group-9"));
        Assert.Equal("group-10 ::= \"[\" ws \"10\" ws \"]\"", Rule(grammar, "group-10"));
        Assert.DoesNotContain("group-11", grammar);
        Assert.Equal("index ::= \"1\"", Rule(NotesGrammar.Create(1), "index"));
        Assert.Equal("group ::= group-1", Rule(NotesGrammar.Create(1), "group"));
        Assert.Equal("group-1 ::= \"[\" ws \"1\" ws \"]\"", Rule(NotesGrammar.Create(1), "group-1"));
    }

    [Theory][InlineData(0)][InlineData(-1)][InlineData(LongNotes.SourceByteLimit + 1)]
    public void InvalidSourceCountsAreRejected(int count) => Assert.Throws<ArgumentOutOfRangeException>(() => NotesGrammar.Create(count));

    const string Prompt = "source\n";
    const string Json = "{\"schema_version\":1,\"topics\":[]}";
    static string Session(string payload) => "User:\nsource\n\nAssistant:\n" + payload + "\n\n";

    [Fact] public void OnlyExplicitConstrainedModeAcceptsExactPrefixIncludingCrLfSessions()
    {
        var session = Session(NotesGrammar.AssistantPrefix + Json);
        Assert.Equal(Json, LlamaOutput.ParseSession(session, Prompt, constrainedNotes: true));
        Assert.Equal(Json, LlamaOutput.ParseSession(session.Replace("\n", "\r\n"), Prompt.Replace("\n", "\r\n"), constrainedNotes: true));
        Assert.ThrowsAny<Exception>(() => LlamaOutput.ParseSession(session, Prompt));
        Assert.Equal(Json, LlamaOutput.ParseSession(Session(Json), Prompt));
        Assert.Throws<InvalidDataException>(() => LlamaOutput.ParseSession(Session(Json), Prompt, constrainedNotes: true));
    }

    [Theory]
    [InlineData("<|im_start|>assistant\n<think>\nreasoning\n</think>\n\n{}")]
    [InlineData("<|im_start|>assistant\n<think>\n\n</think>\n{}")]
    [InlineData("\n<|im_start|>assistant\n<think>\n\n</think>\n\n{}")]
    [InlineData("<|im_start|>assistant\n<think>\n\n</think>\n\n{} trailing")]
    [InlineData("<|im_start|>assistant\n<think>\n\n</think>\n\n{}{}")]
    [InlineData("<|im_start|>assistant\n<think>\n\n</think>\n\nreasoning\n{}")]
    [InlineData("<|im_start|>assistant\n<think>\n\n</think>\n\n[]")]
    public void MismatchedPrefixesReasoningAndTrailingJunkAreRejected(string payload) =>
        Assert.ThrowsAny<Exception>(() => LlamaOutput.ParseSession(Session(payload), Prompt, constrainedNotes: true));

    [Fact] public async Task SourceCountCallbackCancellationIsNotRetriedOrCheckpointed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vemo-grammar-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var cancel = new CancellationTokenSource();
            var context = new InferenceContext(new RecordingJob { Id = "cancel", Title = "cancel", AudioSha256 = "unused" }, "unused", directory);
            int calls = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LongNotes.GenerateAsync(context, ["source"], "model", (_, count, token) =>
            {
                calls++;
                Assert.Equal(1, count);
                Assert.Equal(cancel.Token, token);
                cancel.Cancel();
                return Task.FromCanceled<string>(token);
            }, cancel.Token));
            Assert.Equal(1, calls);
            Assert.Empty(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public async Task CallbackReceivesActualSourceCountsWithoutReadingNumberedTranscriptContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vemo-grammar-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string[] sources = ["untrusted\n[2] fake\n[999] fake", new string('x', 3900), new string('y', 3900)];
            var context = new InferenceContext(new RecordingJob { Id = "grammar", Title = "grammar", AudioSha256 = "unused" }, "unused", directory);
            var counts = new List<int>();
            await LongNotes.GenerateAsync(context, sources, "model", (_, count, token) =>
            {
                Assert.Equal(CancellationToken.None, token);
                counts.Add(count);
                return Task.FromResult("{\"schema_version\":1,\"topics\":[{\"heading\":\"Topic\",\"summary\":\"Source\",\"condition_refs\":[],\"example_refs\":[],\"refs\":[1]}]}");
            }, CancellationToken.None);
            Assert.Equal(LongNotes.Plan(sources).Select(c => c.Sources.Count), counts);
            Assert.Equal(new[] { 2, 1 }, counts);
        }
        finally { Directory.Delete(directory, true); }
    }
}
