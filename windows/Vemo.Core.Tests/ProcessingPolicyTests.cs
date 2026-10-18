using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;
public sealed class ProcessingPolicyTests
{
    sealed class Engine : IInferenceEngine
    {
        public Task<string> TranscribeAsync(InferenceContext c, CancellationToken ct) => Task.FromResult("전사 원문");
        public Task<string> GenerateNotesAsync(InferenceContext c, IReadOnlyList<string> sources, CancellationToken ct) => Task.FromResult("{\"schema_version\":1,\"topics\":[{\"heading\":\"주제\",\"summary\":\"설명\",\"conditions\":[],\"examples\":[],\"refs\":[1]}]}");
    }
    [Fact]
    public async Task Automatic_export_policy_can_be_disabled_without_disabling_manual_export()
    {
        var root = Path.Combine(Path.GetTempPath(), "vemo-policy-" + Guid.NewGuid().ToString("N")).Replace("/var/", "/private/var/");
        try
        {
            using var store = new LibraryStore(Path.Combine(root, "library"));
            store.UpdateSettings(store.Settings with { AutoExport = false, OutputDirectory = Path.Combine(root, "exports") });
            var audio = Path.Combine(root, "fixture.wav"); File.WriteAllBytes(audio, [1, 2, 3]);
            var job = store.Import(audio);
            Assert.True(await new JobRunner(store, new Engine()).RunNextAsync());
            Assert.Equal(JobState.Completed, store.Get(job.Id).State);
            Assert.Null(store.Get(job.Id).ExportPath); Assert.False(Directory.Exists(store.Settings.OutputDirectory));
            Assert.True(File.Exists(store.Export(job.Id)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Input_directory_must_not_overlap_library_or_export_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "vemo-policy-" + Guid.NewGuid().ToString("N")).Replace("/var/", "/private/var/");
        try
        {
            using var store = new LibraryStore(Path.Combine(root, "library"));
            Assert.ThrowsAny<Exception>(() => store.UpdateSettings(store.Settings with { InputDirectory = store.Root }));
            var exports = Path.Combine(root, "exports");
            Assert.ThrowsAny<Exception>(() => store.UpdateSettings(store.Settings with { InputDirectory = exports, OutputDirectory = exports }));
            Assert.Equal("", store.Settings.InputDirectory);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
