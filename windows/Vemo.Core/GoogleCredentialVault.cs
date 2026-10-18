using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace Vemo.Core;

public interface IGoogleCredentialVault
{
    string? Load(string key);
    void Save(string key, string value);
    void Delete(string key);
}

/// Current-user Windows DPAPI; no plaintext fallback on other platforms.
public sealed class GoogleWindowsCredentialVault(string directory) : IGoogleCredentialVault
{
    readonly object gate = new();
    string FileFor(string key)
    {
        if (key is not ("configuration" or "refresh")) throw new ArgumentException("Unknown credential key.", nameof(key));
        var path = Path.Combine(directory, key + ".dpapi"); SafeFiles.NoLinks(path); return path;
    }
    public string? Load(string key)
    {
        lock (gate)
        {
            var path = FileFor(key); if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 131072) throw new GoogleConnectionException("invalid_vault");
            var plain = Crypt(File.ReadAllBytes(path), false);
            try { return new UTF8Encoding(false, true).GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Save(string key, string value)
    {
        lock (gate)
        {
            var path = FileFor(key); var plain = Encoding.UTF8.GetBytes(value);
            try
            {
                if (plain.Length > 65536) throw new GoogleConnectionException("invalid_vault");
                var encrypted = Crypt(plain, true); Directory.CreateDirectory(directory); SafeFiles.NoLinks(directory);
                var temp = Path.Combine(directory, ".tmp-" + Guid.NewGuid());
                try { using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(encrypted); f.Flush(true); } SafeFiles.NoLinks(path); File.Move(temp, path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Delete(string key) { lock (gate) File.Delete(FileFor(key)); }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Crypt(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new GoogleConnectionException("vault_unavailable");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); } }
    }
}
