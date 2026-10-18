using System.Globalization;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public sealed record ProcessingProgress(string JobId, string Stage, double? Percent, TimeSpan Elapsed);
public static class WhisperProgress
{
    public static bool TryPercent(string line, out double percent)
    {
        percent = 0; var match = Regex.Match(line, @"(?:^|\s)progress\s*=\s*(\d+(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return false;
        percent = Math.Clamp(value, 0, 99); return true;
    }
}
