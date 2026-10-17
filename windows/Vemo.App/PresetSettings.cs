using System.Text.Json;
using System.Text.Json.Serialization;
using Vemo.Core;
namespace Vemo.App;

// Loading fills visible fields only. Applying them still uses the normal Save action.
public static class PresetSettings
{
    public static AppSettings Load(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Choose a local settings.json file.");
        using var doc = JsonFormat.Strict(SafeFiles.Read(path));
        var options = new JsonSerializerOptions(JsonFormat.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var preset = doc.Deserialize<AppSettings>(options) ?? throw new InvalidDataException("Invalid settings preset.");
        var model = preset.Models ?? throw new InvalidDataException("Preset has no model settings.");
        if (model.Compute != "CPU") throw new InvalidDataException("Preset import accepts CPU settings only. Configure and confirm CUDA manually in Settings.");
        if (model.WhisperProfile is not ("small" or "large-v3-turbo") || model.NotesProfile is not ("Qwen3.5 9B GGUF" or "Qwen3.5 27B GGUF")) throw new InvalidDataException("Unknown model preset label.");
        if (model.TimeoutMinutes is < 1 or > 1440 || model.GpuLayers is < 0 or > 256) throw new InvalidDataException("Invalid runtime limits.");
        void FilePath(string value, bool executable)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !File.Exists(value)) throw new InvalidDataException("Preset file is missing or not an absolute local path: " + value);
            SafeFiles.NoLinks(value);
            if (executable && !Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Imported runtime paths must name native Windows .exe files, not scripts or command lines.");
        }
        FilePath(model.WhisperExecutable, true); FilePath(model.LlamaExecutable, true);
        FilePath(model.WhisperModel, false); FilePath(model.LlamaModel, false);
        if (!string.IsNullOrWhiteSpace(model.FfmpegExecutable)) FilePath(model.FfmpegExecutable, true);
        if (!string.IsNullOrWhiteSpace(preset.OutputDirectory))
        {
            if (!Path.IsPathFullyQualified(preset.OutputDirectory)) throw new InvalidDataException("Preset output directory must be an absolute local path.");
            SafeFiles.NoLinks(preset.OutputDirectory);
        }
        // No implicit mobile registration, queue change, CUDA confirmation or execution.
        return preset with { MobileDirectory = "", QueuePaused = false, Models = model with { CudaConfirmed = false } };
    }
}
