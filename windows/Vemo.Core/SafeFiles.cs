using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public static partial class SafeFiles
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static void NoLinks(string path)
    {
        var p = Full(path);
        while (!string.IsNullOrEmpty(p))
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links/reparse points are not allowed: " + p); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            p = Path.GetDirectoryName(p);
        }
    }
    public static bool Within(string parent, string child) => Full(child).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Full(parent).Equals(Full(child), StringComparison.OrdinalIgnoreCase);
    public static void Disjoint(string a, string b) { NoLinks(a); NoLinks(b); if (Within(a, b) || Within(b, a)) throw new IOException("Storage, sync and export directories must not overlap."); }
    public static string Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value is "." or ".." || value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || value.EndsWith(' ') || value.EndsWith('.') || Reserved().IsMatch(value)) throw new InvalidDataException("Use a valid Windows name (1–120 characters).");
        return value;
    }
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", RegexOptions.IgnoreCase)] private static partial Regex Reserved();
    public static string FileTitle(string title)
    {
        var value = string.Concat(title.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (value.Length > 90) value = value[..90]; if (value.Length == 0 || Reserved().IsMatch(value)) value = "Recording"; return value;
    }
    public static void Title(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid recording title."); }
    public static void RecordedAt(string? value)
    {
        if (value == null) return;
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new InvalidDataException("Recording date requires a valid ISO date and explicit timezone.");
    }
    public static string Hash(string path) { NoLinks(path); using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
    public static string Read(string path) { NoLinks(path); if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("Text exceeds 4 MiB."); return File.ReadAllText(path); }
    public static void Atomic(string path, string text, bool replace = true)
    {
        NoLinks(path); var dir = Path.GetDirectoryName(path)!; Directory.CreateDirectory(dir); NoLinks(dir);
        var temp = Path.Combine(dir, ".tmp-" + Guid.NewGuid());
        try { using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { using var w = new StreamWriter(f, leaveOpen: true); w.Write(text); w.Flush(); f.Flush(true); } NoLinks(path); File.Move(temp, path, replace); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void DeleteTree(string path) { NoLinks(path); if (!Directory.Exists(path)) return; foreach (var entry in Directory.EnumerateFileSystemEntries(path)) { NoLinks(entry); if (Directory.Exists(entry)) DeleteTree(entry); else File.Delete(entry); } Directory.Delete(path); }
}
