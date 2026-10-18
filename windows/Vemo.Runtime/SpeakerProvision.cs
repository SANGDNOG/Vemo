using Vemo.Core;
using Vemo.Provision;
namespace Vemo.Runtime;

public static class SpeakerProvision
{
    public static async Task PrepareAsync(string directory, HttpClient http, bool offline = false, CancellationToken ct = default)
    {
        SafeFiles.NoLinks(directory); Directory.CreateDirectory(directory);
        var downloads = new PinnedDownloads(http);
        foreach (var pin in SpeakerModels.Pins) await downloads.AcquireAsync(new(pin.File, pin.Url, pin.Bytes, pin.Sha256), directory, offline, ct);
        ct.ThrowIfCancellationRequested(); SpeakerModels.Verify(directory);
        SafeFiles.Atomic(Path.Combine(directory, "MODEL_NOTICES.txt"), SpeakerModels.Notices);
    }
}
