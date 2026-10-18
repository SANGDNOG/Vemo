using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class RuntimeTests
{
    [Fact] public async Task MissingToolsAreExplicitAndPreserveQueueFailure() { var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "vemo-runtime-" + Guid.NewGuid()); Directory.CreateDirectory(root); try { var audio = Path.Combine(root, "source.wav"); File.WriteAllBytes(audio, [1, 2, 3]); using var store = new LibraryStore(Path.Combine(root, "state")); var job = store.Import(audio); await new JobRunner(store, new LocalCliInferenceEngine()).RunNextAsync(); Assert.Equal(JobState.Failed, store.Get(job.Id).State); Assert.Contains("nothing is downloaded", store.Get(job.Id).Error); Assert.Equal("", store.ReadTranscript(job.Id)); } finally { Directory.Delete(root, true); } }
    [Fact] public async Task NativeProcessCancellationAndNonzeroExit() { if (OperatingSystem.IsWindows()) return; var directory = "/private/tmp"; if (!OperatingSystem.IsMacOS()) directory = Path.GetTempPath(); using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync("/bin/sleep", ["30"], directory, 1, cts.Token)); await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunAsync("/usr/bin/false", [], directory, 1, CancellationToken.None)); }
    [Fact] public void StrictJsonRejectsEscapedDuplicateAndInvalidSchema() { Assert.ThrowsAny<Exception>(() => JsonFormat.Strict("{\"schema_version\":1,\"schema_\\u0076ersion\":2}")); Assert.ThrowsAny<Exception>(() => NotesDocument.Parse("{\"schema_version\":1,\"topics\":[{\"heading\":\"h\",\"summary\":\"s\",\"conditions\":[],\"examples\":[],\"refs\":[0]}]}", 1)); }
}
