using System.Text.Json;
using Vemo.App;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;
public sealed class PresetSettingsTests : IDisposable {
    readonly string root=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"vemo-preset-"+Guid.NewGuid());
    public PresetSettingsTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    AppSettings Fixture(){string FilePath(string name){var p=Path.Combine(root,name);File.WriteAllText(p,"fixture, never executed");return p;}return new(){OutputDirectory=Path.Combine(root,"exports"),MobileDirectory="must not register",QueuePaused=true,Models=new(){WhisperExecutable=FilePath("whisper.exe"),LlamaExecutable=FilePath("llama.exe"),FfmpegExecutable=FilePath("ffmpeg.exe"),WhisperModel=FilePath("whisper.bin"),LlamaModel=FilePath("notes.gguf"),CudaConfirmed=true}};}
    string Write(AppSettings s){var p=Path.Combine(root,"settings.json");File.WriteAllText(p,JsonSerializer.Serialize(s,JsonFormat.Options));return p;}
    [Fact] public void LoadsExactLocalPathsWithoutTrustingMobileQueueOrCudaConfirmation(){var s=Fixture();var loaded=PresetSettings.Load(Write(s));Assert.Equal(s.Models.WhisperModel,loaded.Models.WhisperModel);Assert.Equal(s.OutputDirectory,loaded.OutputDirectory);Assert.Equal("",loaded.MobileDirectory);Assert.False(loaded.QueuePaused);Assert.False(loaded.Models.CudaConfirmed);}
    [Fact] public void ScriptCommandLineAndCudaPresetRejected(){var s=Fixture();Assert.ThrowsAny<Exception>(()=>PresetSettings.Load(Write(s with{Models=s.Models with{WhisperExecutable="cmd.exe /c download"}})));Assert.ThrowsAny<Exception>(()=>PresetSettings.Load(Write(s with{Models=s.Models with{Compute="CUDA"}})));}
    [Fact] public void UnknownCommandPropertyAndMissingModelRejected(){var s=Fixture();var p=Write(s);File.WriteAllText(p,File.ReadAllText(p).Replace("\"Models\": {","\"Models\": {\"Command\":\"anything\","));Assert.ThrowsAny<Exception>(()=>PresetSettings.Load(p));Assert.ThrowsAny<Exception>(()=>PresetSettings.Load(Write(s with{Models=s.Models with{LlamaModel=Path.Combine(root,"missing.gguf")}})));}
}
