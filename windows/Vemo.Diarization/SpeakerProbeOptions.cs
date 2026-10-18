using System.Globalization;
namespace Vemo.Diarization;

// Helper-only calibration controls. Application defaults are deliberately unchanged.
public sealed record SpeakerProbeOptions(float Threshold = 0.5f, int NumClusters = -1)
{
    public static SpeakerProbeOptions Parse(IReadOnlyList<string> options)
    {
        var result = new SpeakerProbeOptions(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < options.Count; i += 2)
        {
            var name = options[i];
            if (!seen.Add(name) || i + 1 >= options.Count) throw new ArgumentException("Duplicate or incomplete speaker probe option.");
            var value = options[i + 1];
            switch (name)
            {
                case "--threshold":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) || !float.IsFinite(threshold) || threshold <= 0 || threshold > 2) throw new ArgumentException("--threshold must be finite and greater than 0 through 2 (cosine-distance threshold).");
                    result = result with { Threshold = threshold }; break;
                case "--num-clusters":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var clusters) || clusters is < 1 or > 20) throw new ArgumentException("--num-clusters must be an integer from 1 through 20; omit for automatic clustering.");
                    result = result with { NumClusters = clusters }; break;
                default: throw new ArgumentException("Unknown speaker probe option: " + name);
            }
        }
        return result;
    }
}
