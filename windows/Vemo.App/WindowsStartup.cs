using Microsoft.Win32;

namespace Vemo.App;

/// <summary>Current-user startup registration; only the exact quoted current executable is owned.</summary>
public static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Vemo";
    public static string? Error { get; private set; }
    public static bool IsEnabled
    {
        get
        {
            Error = null;
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return IsOwned(key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), Environment.ProcessPath);
            }
            catch (Exception e) { Error = "시작 프로그램 확인 실패: " + e.Message; return false; }
        }
    }
    /// <returns>False on unsupported systems, a foreign Vemo entry, or registry failure.</returns>
    public static bool SetEnabled(bool enabled)
    {
        Error = null;
        if (!OperatingSystem.IsWindows()) { Error = "Windows에서만 시작 프로그램을 설정할 수 있습니다."; return false; }
        try
        {
            var command = Command(Environment.ProcessPath);
            using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey) : Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return !enabled;
            return Apply(key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), Environment.ProcessPath, enabled,
                () => { if (OperatingSystem.IsWindows()) key.SetValue(ValueName, command, RegistryValueKind.String); },
                () => { if (OperatingSystem.IsWindows()) key.DeleteValue(ValueName, false); });
        }
        catch (Exception e) { Error = "시작 프로그램 설정 실패: " + e.Message; return false; }
    }
    internal static string Command(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || executable.Contains('"') || executable.Contains('\r') || executable.Contains('\n'))
            throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
        return "\"" + executable + "\"";
    }
    internal static bool IsOwned(object? existing, string? executable) => existing is string text
        && executable != null && string.Equals(text, Command(executable), StringComparison.OrdinalIgnoreCase);
    internal static bool Apply(object? existing, string? executable, bool enabled, Action write, Action remove)
    {
        if (existing != null && !IsOwned(existing, executable))
        {
            Error = "다른 프로그램이 등록한 Vemo 시작 항목을 변경하지 않았습니다."; return false;
        }
        if (enabled) { if (existing == null) write(); }
        else if (existing != null) remove();
        return true;
    }
}
