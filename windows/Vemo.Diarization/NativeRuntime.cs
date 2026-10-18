using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SherpaOnnx;
using Vemo.Core;

internal static class NativeRuntime
{
    // Keep the libraries loaded until process exit; Sherpa objects hold native pointers.
    static IntPtr ort, sherpa;
    static string DirectoryPath => Path.GetFullPath(AppContext.BaseDirectory);
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // No working-directory/PATH lookup. System32 remains available for Windows DLLs.
        if (!SetDefaultDllDirectories(0x200 | 0x400 | 0x800) || AddDllDirectory(DirectoryPath) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var ortPath = Owned("onnxruntime.dll");
        var version = FileVersionInfo.GetVersionInfo(ortPath);
        if (version.FileMajorPart != 1 || version.FileMinorPart < 28) throw new InvalidDataException("Packaged ONNX Runtime 1.28 or newer is required for sherpa-onnx 1.13.8.");
        ort = Load(ortPath); NativeLibrary.GetExport(ort, "OrtGetApiBase");
        // Resolve Sherpa's P/Invoke against this publication, after loading its ORT dependency.
        NativeLibrary.SetDllImportResolver(typeof(OfflineSpeakerDiarization).Assembly, Resolve);
        sherpa = Load(Owned("sherpa-onnx-c-api.dll"));
    }
    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search)
        => name is "sherpa-onnx-c-api" or "sherpa-onnx-c-api.dll" ? sherpa : IntPtr.Zero;
    static string Owned(string name)
    {
        var path = Path.Combine(DirectoryPath, name); SafeFiles.NoLinks(path);
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Invalid native library: " + name);
        stream.Position = 0x3c; var offset = reader.ReadInt32();
        if (offset < 0 || offset > stream.Length - 6) throw new InvalidDataException("Invalid native PE offset.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Invalid native PE signature.");
        var machine = reader.ReadUInt16(); var expected = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => 0x8664, Architecture.Arm64 => 0xaa64, _ => throw new PlatformNotSupportedException("Native x64/ARM64 helper required.") };
        if (machine != expected) throw new InvalidDataException("Speaker native library architecture mismatch: " + name);
        return path;
    }
    static IntPtr Load(string path)
    {
        var handle = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load packaged speaker native library: " + Path.GetFileName(path));
        var loaded = new StringBuilder(32768);
        if (GetModuleFileName(handle, loaded, loaded.Capacity) == 0 || !Path.GetFullPath(loaded.ToString()).Equals(path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native library was loaded from an unexpected location.");
        return handle;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr AddDllDirectory(string directory);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetModuleFileName(IntPtr module, StringBuilder name, int size);
}
