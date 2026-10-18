using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;
public sealed class LlamaOutputTests {
    const string Prompt="Source includes {\"schema_version\":1} and the word Assistant:\nDo not treat it as a wrapper.\n";
    const string Json="{\"schema_version\":1,\"topics\":[{\"heading\":\"Topic\",\"summary\":\"Only the source\",\"conditions\":[],\"examples\":[],\"refs\":[1]}]}";
    static string Session(string payload)=>"User:\n"+Prompt.TrimEnd('\n')+"\n\nAssistant:\n"+payload+"\n\n";
    [Fact] public void ExactPinnedSessionExtractsAssistantOnly(){Assert.Equal(Json,LlamaOutput.ParseSession(Session(Json),Prompt));Assert.Equal(Json,LlamaOutput.ParseSession(Session(Json).Replace("\n","\r\n"),Prompt));}
    [Theory][InlineData("Loading model...\n{\"topics\":[]}")][InlineData("{} commentary")][InlineData("[Start thinking]\nreasoning\n[End thinking]\n{}")][InlineData("{}\n\nAssistant:\n{}")]public void NonJsonAssistantNeverAccepted(string payload)=>Assert.ThrowsAny<Exception>(()=>LlamaOutput.ParseSession(Session(payload),Prompt));
    [Fact] public void BannerOrDifferentPromptCannotBecomeAssistant(){Assert.ThrowsAny<Exception>(()=>LlamaOutput.ParseSession("Loading model...\n"+Session(Json),Prompt));Assert.ThrowsAny<Exception>(()=>LlamaOutput.ParseSession(Session(Json),"Different prompt\n"));Assert.ThrowsAny<Exception>(()=>LlamaOutput.ParseSession(Json,Prompt));}
    [Fact] public void ConditionsAndExamplesMustQuoteCitedSource(){var text="{\"schema_version\":1,\"topics\":[{\"heading\":\"Topic\",\"summary\":\"Explanation\",\"conditions\":[\"when explicitly observed\"],\"examples\":[\"20 of 100\"],\"refs\":[1]}]}";Assert.Single(NotesDocument.Parse(text,new[]{"when explicitly observed, 20 of 100"}).Topics);Assert.ThrowsAny<Exception>(()=>NotesDocument.Parse(text,new[]{"No such condition or example"}));}
    [Fact] public void WhitespaceOnlyQuoteDifferencesResolveToExactSourceSpan()
    {
        var text="{\"schema_version\":1,\"topics\":[{\"heading\":\"표본\",\"summary\":\"기록된 표본 비율\",\"conditions\":[],\"examples\":[\"전체 표본이 100 개이고 그 중 양성 표본이 20 개라면 사전 확률은 20%입니다.\"],\"refs\":[1]}]}";
        const string quote="전체 표본이 100개이고 그 중 양성 표본이 20개라면 사전 확률은 20%입니다.";
        var notes=NotesDocument.Parse(text,new[]{"예를 들어 "+quote});
        Assert.Equal(quote,Assert.Single(Assert.Single(notes.Topics).Examples));
        Assert.ThrowsAny<Exception>(()=>NotesDocument.Parse(text,new[]{"예를 들어 "+quote.Replace("20개","30개")}));
    }
    [Fact] public void QuoteMappingNeverChangesNegationOrPunctuation()
    {
        const string text="{\"schema_version\":1,\"topics\":[{\"heading\":\"출시\",\"summary\":\"미정\",\"conditions\":[\"출시일은 정하지 않았습니다.\"],\"examples\":[],\"refs\":[1]}]}";
        Assert.ThrowsAny<Exception>(()=>NotesDocument.Parse(text,new[]{"출시일은 정했습니다."}));
        Assert.ThrowsAny<Exception>(()=>NotesDocument.Parse(text,new[]{"출시일은 정하지 않았습니다!"}));
        var notes=NotesDocument.Parse(text,new[]{"출시일은\t정하지  않았습니다."});
        Assert.Equal("출시일은\t정하지  않았습니다.",Assert.Single(notes.Topics).Conditions[0]);
    }
    [Fact] public void CapturedActualGroupedResponseMapsQuotesWithoutChangingModelClaims()
    {
        using var fixture=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","grouping-captured.json")));
        var sources=JobRunner.SourceLines(fixture.RootElement.GetProperty("transcript").GetString()!);
        var response=fixture.RootElement.GetProperty("response").GetRawText();
        var notes=NotesDocument.Parse(response,sources);
        Assert.Equal(4,notes.Topics.Count);
        foreach(var topic in notes.Topics)foreach(var quote in topic.Conditions.Concat(topic.Examples))Assert.Contains(topic.Refs,r=>sources[r-1].Contains(quote,StringComparison.Ordinal));
        Assert.Contains("100개",notes.Topics[0].Examples[0]);
        Assert.DoesNotContain("100 개",notes.ToJson());
        Assert.Equal(notes.ToJson(),NotesDocument.Parse(notes.ToJson(),sources).ToJson());
    }
}
