using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class SpeakerDocumentTests
{
    const string AudioHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Text = "첫 문장. 다음 문장.";
    static TranscriptDocument Transcript => new(Text, [new(0, 2, "첫 문장."), new(2, 4, " 다음 문장.")]);
    static SpeakerDocument Create() => SpeakerDocument.Create(AudioHash, Transcript,
        [new SpeakerTurn(2, 4, "cluster_a"), new SpeakerTurn(0, 2, "cluster_z")]);
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact] public void CanonicalWireContractUsesExactHashesAndFirstOnsetLabels()
    {
        var document = Create(); var json = JsonNode.Parse(document.Serialize())!.AsObject();
        Assert.Equal(1, json["schema_version"]!.GetValue<int>());
        Assert.Equal(AudioHash, json["audio_sha256"]!.GetValue<string>());
        Assert.Equal(Hash(Text), json["transcript_sha256"]!.GetValue<string>());
        Assert.True(Guid.TryParse(json["generation"]!.GetValue<string>(), out _));
        var speakers = json["speakers"]!.AsArray();
        Assert.Equal("speaker_1", speakers[0]!["id"]!.GetValue<string>());
        Assert.Equal("화자 1", speakers[0]!["name"]!.GetValue<string>());
        Assert.Equal("speaker_2", speakers[1]!["id"]!.GetValue<string>());
        Assert.Equal("speaker_1", json["segments"]![0]!["speaker_id"]!.GetValue<string>());
        Assert.Equal("speaker_2", json["segments"]![1]!["speaker_id"]!.GetValue<string>());
        Assert.Equal(Text, string.Concat(json["segments"]!.AsArray().Select(s => s!["text"]!.GetValue<string>())));
        SpeakerDocument.Parse(document.Serialize(), AudioHash, Text);
    }

    [Fact] public void StaleExactTranscriptOrAudioRejectedEvenWhenNormalizationMatches()
    {
        var json = Create().Serialize();
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Parse(json, new string('b', 64), Text));
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Parse(json, AudioHash, Text + " "));
    }

    [Theory]
    [InlineData("schema_version", "2")]
    [InlineData("audio_sha256", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    [InlineData("generation", "\"not-a-uuid\"")]
    [InlineData("speakers", "[{\"id\":\"speaker_1\",\"name\":\"one\"},{\"id\":\"speaker_1\",\"name\":\"two\"}]")]
    [InlineData("speakers", "[{\"id\":\"speaker_1\",\"name\":\" \"}]")]
    [InlineData("speakers", "[{\"id\":\"speaker_1\",\"name\":\"bad\\nname\"}]")]
    [InlineData("turns", "[{\"start_s\":-1,\"end_s\":2,\"speaker_id\":\"speaker_1\"}]")]
    [InlineData("turns", "[{\"start_s\":2,\"end_s\":1,\"speaker_id\":\"speaker_1\"}]")]
    [InlineData("turns", "[{\"start_s\":0,\"end_s\":3600.01,\"speaker_id\":\"speaker_1\"}]")]
    [InlineData("turns", "[{\"start_s\":0,\"end_s\":2,\"speaker_id\":\"missing\"}]")]
    [InlineData("segments", "[{\"start_s\":0,\"end_s\":4,\"text\":\"fabricated\",\"speaker_id\":null}]")]
    public void InvalidWireFieldsRejected(string key, string replacement)
    {
        var json = JsonNode.Parse(Create().Serialize())!; json[key] = JsonNode.Parse(replacement);
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Parse(json.ToJsonString(), AudioHash, Text));
    }

    [Fact] public void UnknownSegmentReferenceDuplicateJsonKeysAndOversizeRejected()
    {
        var json = JsonNode.Parse(Create().Serialize())!; json["segments"]![0]!["speaker_id"] = "missing";
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Parse(json.ToJsonString(), AudioHash, Text));
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Parse(Create().Serialize().Insert(1, "\"schema_version\":1,"), AudioHash, Text));
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Parse(new string(' ', 8 * 1024 * 1024) + Create().Serialize(), AudioHash, Text));
    }

    [Fact] public void MoreThanOneHundredThousandTurnsRejected()
    {
        var turns = Enumerable.Repeat(new SpeakerTurn(0, 1, "a"), 100001).ToArray();
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Create(AudioHash, Transcript, turns));
    }

    [Theory]
    [InlineData(0, 1, null)] // Exactly half is insufficient.
    [InlineData(0, 1.01, "speaker_1")]
    public void AssignmentRequiresStrictMajority(double start, double end, string? expected)
    {
        var document = SpeakerDocument.Create(AudioHash, new("original", [new(0, 2, "original")]), [new SpeakerTurn(start, end, "a")]);
        var json = JsonNode.Parse(document.Serialize())!;
        Assert.Equal(expected, json["segments"]![0]!["speaker_id"]?.GetValue<string>());
    }

    [Fact] public void AnyCompetingOverlapAndUnknownTimingRemainUnassigned()
    {
        var document = SpeakerDocument.Create(AudioHash, new("original", [new(0, 4, "original")]),
            [new SpeakerTurn(0, 4, "a"), new SpeakerTurn(3.9, 4, "b")]);
        Assert.Null(JsonNode.Parse(document.Serialize())!["segments"]![0]!["speaker_id"]);
        // A long display row must not be assigned by splitting words or interpolating timing.
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Create(AudioHash, new("untimed", []), [new SpeakerTurn(0, 4, "a")]));
    }

    [Fact] public void SequentialSecondSpeakerMakesCoarseSegmentAmbiguous()
    {
        var document = SpeakerDocument.Create(AudioHash, new("original", [new(0, 4, "original")]),
            [new SpeakerTurn(0, 3.5, "a"), new SpeakerTurn(3.5, 4, "b")]);
        Assert.Null(JsonNode.Parse(document.Serialize())!["segments"]![0]!["speaker_id"]);
    }

    [Fact] public void DuplicateSameSpeakerIntervalsCannotInflateCoverage()
    {
        var document = SpeakerDocument.Create(AudioHash, new("original", [new(0, 4, "original")]),
            [new SpeakerTurn(0, 1.5, "a"), new SpeakerTurn(0, 1.5, "a")]);
        Assert.Null(JsonNode.Parse(document.Serialize())!["segments"]![0]!["speaker_id"]);
    }

    [Fact] public void LongOriginalSegmentRemainsOneConservativeAssignment()
    {
        var text = new string('가', 700);
        var document = SpeakerDocument.Create(AudioHash, new(text, [new(0, 10, text)]),
            [new SpeakerTurn(0, 5, "a"), new SpeakerTurn(5, 10, "b")]);
        var segment = Assert.Single(JsonNode.Parse(document.Serialize())!["segments"]!.AsArray());
        Assert.Equal(text, segment!["text"]!.GetValue<string>()); Assert.Null(segment["speaker_id"]);
    }
    [Theory]
    [InlineData("a\u200db")]
    [InlineData("a\ue000b")]
    [InlineData("a\u0378b")]
    [InlineData("a\u0000b")]
    public void UnicodeOtherCategoriesRejected(string name) => Assert.Throws<InvalidDataException>(() => SpeakerDocument.ValidateName(name));

    [Fact] public void MalformedUtf16NameRejectedWithoutTestRunnerSerialization()
    {
        var name = new string(new[] { 'a', '\ud800', 'b' });
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.ValidateName(name));
    }

    [Fact] public void DisjointIntervalsDoNotConsumeCartesianCandidateBudget()
    {
        var segments = Enumerable.Range(0, 1500).Select(i => new TranscriptSegment(i, i + 1, "a")).ToArray();
        var turns = Enumerable.Range(0, 1500).Select(i => new SpeakerTurn(i, i + 1, "one")).ToArray();
        Assert.Equal(1500, SpeakerDocument.Create(AudioHash, new(new string('a', 1500), segments), turns).Segments.Count);
    }
    [Fact] public void PathologicalOverlappingCandidatesAreStillBounded()
    {
        var segments = Enumerable.Range(0, 1500).Select(_ => new TranscriptSegment(0, 3000, "a")).ToArray();
        var turns = Enumerable.Range(0, 1500).Select(_ => new SpeakerTurn(0, 3000, "one")).ToArray();
        Assert.Throws<InvalidDataException>(() => SpeakerDocument.Create(AudioHash, new(new string('a', 1500), segments), turns));
    }
}
