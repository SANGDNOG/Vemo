using System.Globalization;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public static class RecordingTitles
{
    public static void ValidateDay(string? day)
    {
        if (day != null && (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != day)) throw new InvalidDataException("Recording date requires YYYY-MM-DD.");
    }
    public static string BaseTitle(string title)
    {
        var clean = title.Trim();
        while (Regex.Match(clean, @" — (\d{4}-\d{2}-\d{2})$") is { Success: true } match)
        {
            try { ValidateDay(match.Groups[1].Value); } catch (InvalidDataException) { break; }
            clean = clean[..match.Index].TrimEnd();
        }
        SafeFiles.Title(clean); return clean;
    }
    public static string? Day(RecordingJob job) => job.RecordingDate ?? job.RecordedAt?[..10];
    public static string Display(RecordingJob job)
    { var title = BaseTitle(job.Title); var day = Day(job); ValidateDay(day); return day == null ? title : title + " — " + day; }
}
