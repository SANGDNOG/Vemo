using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class ProcessingProgressTests
{
    sealed class Engine : IInferenceEngine
    {
        public readonly TaskCompletionSource EnteredNotes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> TranscribeAsync(InferenceContext context, CancellationToken ct)
        { context.Progress?.Report(new(context.Job.Id, "transcribing", 104, TimeSpan.Zero)); return Task.FromResult("fixture transcript"); }
        public async Task<string> GenerateNotesAsync(InferenceContext context, IReadOnlyList<string> sources, CancellationToken ct)
        {
            EnteredNotes.SetResult(); await Finish.Task.WaitAsync(ct);
            // Late ASR reports must not replace the notes stage.
            context.Progress?.Report(new(context.Job.Id, "transcribing", 80, TimeSpan.Zero));
            return "{\"schema_version\":1,\"topics\":[{\"heading\":\"heading\",\"summary\":\"summary\",\"conditions\":[],\"examples\":[],\"refs\":[1]}]}";
        }
    }
    [Fact] public async Task LiveSnapshotTracksNotesElapsedAndRejectsLateAsrReport()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-progress-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "audio.wav"); File.WriteAllBytes(audio, [1]); using var store = new LibraryStore(Path.Combine(root, "state"));
            store.UpdateSettings(new() { AutoExport = false }); var job = store.Import(audio); var engine = new Engine(); var runner = new JobRunner(store, engine); var stages = new List<ProcessingProgress>();
            runner.ProgressChanged += (_, _) => { var p = runner.Progress; if (p != null) stages.Add(p); };
            var pending = runner.RunNextAsync(); await engine.EnteredNotes.Task;
            var before = runner.Progress!; Assert.Equal("notes", before.Stage); Assert.Null(before.Percent); await Task.Delay(20); Assert.True(runner.Progress!.Elapsed > before.Elapsed);
            engine.Finish.SetResult(); await pending; Assert.Equal("completed", runner.Progress!.Stage); Assert.Null(runner.ActiveJobId);
            Assert.Contains(stages, p => p.Stage == "transcribing" && p.Percent == 99); Assert.Equal(JobState.Completed, store.Get(job.Id).State);
            var last = runner.Progress.Elapsed; await Task.Delay(10); Assert.Equal(last, runner.Progress.Elapsed);
        }
        finally { Directory.Delete(root, true); }
    }
}
