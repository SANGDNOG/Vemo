using System.Runtime.InteropServices;
namespace Vemo.App;

internal static class WindowsPower
{
    [StructLayout(LayoutKind.Sequential)]
    struct Status
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out Status status);
    internal static bool HasExternalPower()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return GetSystemPowerStatus(out var status) && status.ACLineStatus == 1;
    }
}
