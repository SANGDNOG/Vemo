using System.Reflection;
using System.Text.Json;
using Vemo.Core.Tests;
using Vemo.Core;
using Xunit;
if (args.FirstOrDefault() == "--child-sleep") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (args.FirstOrDefault() == "--child-fail") return 7;
if (args.FirstOrDefault() == "--infer") return await RealInference.Run(args);
// Executes the same acceptance tests as dotnet test, on the native target runtime.
// No model-quality claim: all inference fixtures are deliberately synthetic.
var results = new List<object>(); int failures = 0;
foreach (var type in typeof(LifecycleTests).Assembly.GetTypes().Where(t => t.IsPublic && t.Namespace == typeof(LifecycleTests).Namespace))
foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<FactAttribute>() != null))
{
    var data = method.GetCustomAttribute<TheoryAttribute>() != null
        ? method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray()
        : new object?[][] { [] };
    if (data.Length == 0) { failures++; results.Add(new { test = type.Name + "." + method.Name, passed = false, error = "No supported inline theory data; case was not silently skipped." }); continue; }
    foreach (var arguments in data)
    {
        var name = type.Name + "." + method.Name + (arguments.Length == 0 ? "" : JsonSerializer.Serialize(arguments));
        if (OperatingSystem.IsWindows() && method.Name == "NativeProcessCancellationAndNonzeroExit") { results.Add(new { test = name, skipped = true, reason = "Unix subprocess test; native Windows subprocess test is reported separately." }); continue; }
        var started = DateTimeOffset.UtcNow; string? error = null; object? instance = null;
        try { instance = Activator.CreateInstance(type)!; var value = method.Invoke(instance, arguments); if (value is Task task) await task; }
        catch (Exception e) { failures++; error = (e is TargetInvocationException ? e.InnerException : e)?.ToString(); }
        finally { try { (instance as IDisposable)?.Dispose(); } catch (Exception e) { if (error == null) failures++; error += "\nCleanup: " + e.Message; } }
        results.Add(new { test = name, passed = error == null, error, elapsed_ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds });
    }
}
if(OperatingSystem.IsWindows()) {
    string? error=null;
    try {
        var exe=Environment.ProcessPath!;var work=Path.GetDirectoryName(exe)!;
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try{await ProcessRunner.RunAsync(exe,["--child-sleep"],work,1,cancel.Token);throw new Exception("Child was not cancelled.");}catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
        try{await ProcessRunner.RunAsync(exe,["--child-fail"],work,1,CancellationToken.None);throw new Exception("Nonzero exit was not rejected.");}catch(IOException e)when(e.Message.Contains("exited 7")){}
    }catch(Exception e){error=e.ToString();failures++;}
    results.Add(new{test="NativeWindowsProcessCancellationAndNonzeroExit",passed=error==null,error});
}
var report = JsonSerializer.Serialize(new { schema_version = 1, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), runtime = Environment.Version.ToString(), actual_model_inference = false, passed = failures == 0, failures, results }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(report); if (args.Length > 0) { var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, report); }
return failures == 0 ? 0 : 1;

static class RealInference
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Usage: Vemo.Validation --infer <settings.json> <audio> <report.json>"); return 2; }
        var report = Path.GetFullPath(args[3]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var root = Path.Combine(Path.GetDirectoryName(report)!, "inference-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var started = DateTimeOffset.UtcNow; object result; int code;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[1]), JsonFormat.Options) ?? throw new InvalidDataException("Invalid settings.");
            using var store = new LibraryStore(Path.Combine(root, "state")); store.UpdateSettings(settings with { QueuePaused = false, MobileDirectory = "", OutputDirectory = Path.Combine(root, "exports") });
            var job = store.Import(Path.GetFullPath(args[2])); await new JobRunner(store, new LocalCliInferenceEngine()).RunNextAsync(); job = store.Get(job.Id);
            code = job.State == JobState.Completed ? 0 : 1;
            result = new { schema_version = 1, actual_model_inference = true, passed = code == 0, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), elapsed_seconds = (DateTimeOffset.UtcNow - started).TotalSeconds, state = job.State.ToString(), job.Error, job.ExportError, job.ExportPath, job.AudioSha256, job.Models, transcript = store.ReadTranscript(job.Id), notes = store.ReadNotes(job.Id), isolated_root = root };
        }
        catch (Exception e) { code = 1; result = new { schema_version = 1, actual_model_inference = true, passed = false, error = e.ToString(), isolated_root = root }; }
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(report, json); Console.WriteLine(json); return code;
    }
}
