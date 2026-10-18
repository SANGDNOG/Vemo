using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class SpeakerLifecycleTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "vemo-speaker-lifecycle-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    RecordingJob Complete(LibraryStore store)
    {
        Directory.CreateDirectory(root); var audio = Path.Combine(root, "test.wav"); File.WriteAllBytes(audio, [1, 2, 3]);
        var job = store.Import(audio); store.TryStart(job.Id); store.SaveTranscript(job.Id, "original", [new(0, 4, "original")]); store.Complete(job.Id, "notes"); return store.Get(job.Id);
    }
    sealed class UnusedInference : IInferenceEngine
    {
        public Task<string> TranscribeAsync(InferenceContext c, CancellationToken ct) => throw new InvalidOperationException();
        public Task<string> GenerateNotesAsync(InferenceContext c, IReadOnlyList<string> sources, CancellationToken ct) => throw new InvalidOperationException();
    }
    sealed class Analyze(Func<InferenceContext, CancellationToken, Task<IReadOnlyList<SpeakerTurn>>> action) : ISpeakerAnalysisEngine
    { public Task<IReadOnlyList<SpeakerTurn>> AnalyzeAsync(InferenceContext c, CancellationToken ct) => action(c, ct); }
    [Fact] public async Task CancellationKeepsCommittedArtifactsAndReleasesSharedGate()
    {
        using var store = new LibraryStore(Path.Combine(root, "state")); var job = Complete(store);
        var old = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "a")]); store.SaveSpeakerDocument(job.Id, old);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new JobRunner(store, new UnusedInference());
        var pending = runner.AnalyzeSpeakersAsync(job.Id, new Analyze(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return []; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await runner.RunNextAsync()); runner.CancelActive();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(runner.ActiveJobId); Assert.Equal(old.Serialize(), store.ReadSpeakerDocument(job.Id)!.Serialize());
        Assert.Equal("original", store.ReadTranscript(job.Id)); Assert.Equal("notes", store.ReadNotes(job.Id));
        await runner.AnalyzeSpeakersAsync(job.Id, new Analyze((_, _) => Task.FromResult<IReadOnlyList<SpeakerTurn>>([new(0, 4, "b")])));
        Assert.NotEqual(old.Generation, store.ReadSpeakerDocument(job.Id)!.Generation);
    }
    [Fact] public async Task RenameDuringAnalysisRejectsReplacement()
    {
        using var store = new LibraryStore(Path.Combine(root, "state")); var job = Complete(store);
        var old = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "a")]); store.SaveSpeakerDocument(job.Id, old);
        var runner = new JobRunner(store, new UnusedInference());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.AnalyzeSpeakersAsync(job.Id, new Analyze((_, _) =>
        {
            store.RenameSpeaker(job.Id, old.Generation, "speaker_1", "renamed");
            return Task.FromResult<IReadOnlyList<SpeakerTurn>>([new(0, 4, "a")]);
        })));
        Assert.Equal("renamed", store.ReadSpeakerDocument(job.Id)!.Speakers[0].Name);
        Assert.Equal(old.Generation, store.ReadSpeakerDocument(job.Id)!.Generation);
    }
    [Fact] public void CommitChecksExpectedRecordingInsideStoreGate()
    {
        using var store = new LibraryStore(Path.Combine(root, "state")); var job = Complete(store);
        var doc = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "a")]);
        store.Rename(job.Id, "changed");
        Assert.Throws<InvalidDataException>(() => store.SaveSpeakerDocument(job.Id, doc, expectedJob: job));
        Assert.Null(store.ReadSpeakerDocument(job.Id));
    }
    [Fact] public async Task OptionalEngineFailureKeepsCompletedTranscriptNotesAndSpeakers()
    {
        using var store = new LibraryStore(Path.Combine(root, "state")); var job = Complete(store);
        var old = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "a")]); store.SaveSpeakerDocument(job.Id, old);
        var runner = new JobRunner(store, new UnusedInference());
        await Assert.ThrowsAsync<IOException>(() => runner.AnalyzeSpeakersAsync(job.Id, new Analyze((_, _) => throw new IOException("optional runtime unavailable"))));
        Assert.Equal(JobState.Completed, store.Get(job.Id).State); Assert.Equal(old.Serialize(), store.ReadSpeakerDocument(job.Id)!.Serialize());
        Assert.Equal("original", store.ReadTranscript(job.Id)); Assert.Equal("notes", store.ReadNotes(job.Id)); Assert.Null(runner.ActiveJobId);
    }
    [Fact] public void CancelledCommitNeverReplacesOldSidecar()
    {
        using var store = new LibraryStore(Path.Combine(root, "state")); var job = Complete(store);
        var old = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "a")]); store.SaveSpeakerDocument(job.Id, old);
        var next = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "b")]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.SaveSpeakerDocument(job.Id, next, ct: cancellation.Token));
        Assert.Equal(old.Serialize(), store.ReadSpeakerDocument(job.Id)!.Serialize());
    }
    [Fact] public async Task TooManyClustersFailsWithCountGuidanceAndPreservesPreviousResult()
    {
        using var store = new LibraryStore(Path.Combine(root, "state")); var job = Complete(store);
        var old = SpeakerDocument.Create(job.AudioSha256, store.ReadTranscriptDocument(job.Id), [new(0, 4, "old")]); store.SaveSpeakerDocument(job.Id, old);
        var runner = new JobRunner(store, new UnusedInference());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => runner.AnalyzeSpeakersAsync(job.Id, new Analyze((_, _) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>(Enumerable.Range(0, 21).Select(n => new SpeakerTurn(n / 10d, (n + 1) / 10d, "cluster_" + n)).ToArray()))));
        Assert.Contains("화자 수를 지정해 다시 분석", error.Message);
        Assert.Equal(old.Serialize(), store.ReadSpeakerDocument(job.Id)!.Serialize()); Assert.Equal(JobState.Completed, store.Get(job.Id).State);
        Assert.Equal("original", store.ReadTranscript(job.Id)); Assert.Equal("notes", store.ReadNotes(job.Id));
    }
}
