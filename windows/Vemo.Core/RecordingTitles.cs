using System.Globalization;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public static class RecordingTitles
{
    // Reference coverage is deterministic prominence; ties keep the validated document's topic order.
    // Avoid generic scaffolding and use at most two distinct topic headings, with no additional inference.
    public static string? FromNotes(NotesDocument notes)
    {
        static string Clean(string heading)
        {
            var clean = Regex.Replace(heading, @"[\p{Cc}\p{Cf}<>:""/\\|?*#`]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('.');
            if (clean.Length == 0) return "";
            // Headings may be much longer than titles; reduce before title validation.
            if (clean.Length > 1024) clean = clean[..(char.IsHighSurrogate(clean[1023]) ? 1023 : 1024)];
            try { clean = BaseTitle(clean); } catch (InvalidDataException) { return ""; }
            if (Regex.IsMatch(clean, @"^(overview|introduction|summary|conclusion|notes|topic|주제|개요|서론|요약|결론|정리|노트)(\s*\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "";
            return clean;
        }
        var headings = notes.Topics.Select((topic, index) => new { Text = Clean(topic.Heading), Coverage = topic.Refs.Distinct().Count(), Index = index })
            .Where(topic => topic.Text.Length > 0).OrderByDescending(topic => topic.Coverage).ThenBy(topic => topic.Index)
            .Select(topic => topic.Text).Distinct(StringComparer.OrdinalIgnoreCase);
        string? title = null;
        foreach (var heading in headings)
        {
            var candidate = heading;
            if (candidate.Length > 80)
            {
                var length = char.IsHighSurrogate(candidate[79]) ? 79 : 80;
                candidate = candidate[..length].TrimEnd().TrimEnd('.');
            }
            try { SafeFiles.Name(candidate); } catch (InvalidDataException) { continue; }
            if (title == null) title = candidate;
            else { if (title.Length + 3 + candidate.Length <= 80) title += " · " + candidate; break; }
        }
        return title;
    }
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
