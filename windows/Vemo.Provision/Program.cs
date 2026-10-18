using System.Text.Json;
using Vemo.Core;
using Vemo.Provision;

if(args.Contains("--help")||args.Length==0){Console.WriteLine("Vemo.Provision --destination <new directory> --arch x64|arm64 --whisper small|turbo --notes 9b|27b [--cache <archive/model directory>] [--existing-whisper <exe>] [--existing-llama <exe>] [--existing-asr-model <file>] [--existing-notes-model <file>] [--ffmpeg <exe> | --download-ffmpeg | --wav-only] [--offline]\nDownloads only selected pinned CPU runtimes/models; no weights are redistributed. Existing model files must match the selected pin. --wav-only prepares a direct whisper WAV smoke test, not a complete app pipeline. Existing executables are user-selected and recorded as unpinned.");return args.Length==0?2:0;}
try {
    if(args[0]=="--speaker-models") {
        if(args.Length is < 2 or > 3 || (args.Length==3 && args[2]!="--offline")) throw new ArgumentException("Vemo.Provision --speaker-models <directory> [--offline]");
        using var speakerHttp=new HttpClient(){Timeout=Timeout.InfiniteTimeSpan};
        await Vemo.Runtime.SpeakerProvision.PrepareAsync(SafeFiles.Full(args[1]),speakerHttp,args.Length==3);
        Console.WriteLine("Pinned optional speaker models verified. Windows helper must be published separately; inference has not been tested."); return 0;
    }
    var options=new Dictionary<string,string>();var flags=new HashSet<string>();
    var allowed=new HashSet<string>{"--destination","--arch","--whisper","--notes","--cache","--existing-whisper","--existing-llama","--existing-asr-model","--existing-notes-model","--ffmpeg"};
    for(int i=0;i<args.Length;i++){if(args[i] is "--offline" or "--wav-only" or "--download-ffmpeg"){flags.Add(args[i]);continue;}if(!allowed.Contains(args[i])||i+1==args.Length||args[i+1].StartsWith("--"))throw new ArgumentException("Unknown or incomplete option: "+args[i]);if(!options.TryAdd(args[i],args[++i]))throw new ArgumentException("Duplicate option.");}
    string Required(string key)=>options.GetValueOrDefault(key)??throw new ArgumentException("Missing "+key);
    var arch=Required("--arch");if(arch is not ("x64" or "arm64"))throw new ArgumentException("Architecture must be x64 or arm64.");
    var asr=Required("--whisper");if(asr is not ("small" or "turbo"))throw new ArgumentException("Whisper must be small or turbo.");
    var notes=Required("--notes");if(notes is not ("9b" or "27b"))throw new ArgumentException("Notes must be 9b or 27b.");
    if(new[]{flags.Contains("--wav-only"),flags.Contains("--download-ffmpeg"),options.ContainsKey("--ffmpeg")}.Count(x=>x)!=1)throw new ArgumentException("Choose exactly one: --ffmpeg <installed exe>, --download-ffmpeg, or --wav-only.");
    if(flags.Contains("--download-ffmpeg")&&arch!="x64")throw new ArgumentException("The pinned FFmpeg download is x64 only. ARM64 must select an existing native ARM64 FFmpeg or --wav-only; emulation is not selected automatically.");
    var destination=SafeFiles.Full(Required("--destination"));SafeFiles.NoLinks(destination);if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new IOException("Destination must be new or empty; existing installations are preserved.");
    var cache=SafeFiles.Full(options.GetValueOrDefault("--cache")??Path.Combine(Path.GetDirectoryName(destination)!,"vemo-download-cache"));SafeFiles.Disjoint(cache,destination);Directory.CreateDirectory(cache);Directory.CreateDirectory(destination);
    using var catalogue=JsonFormat.Strict(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"runtime-catalogue.json")));if(catalogue.RootElement.GetProperty("schema_version").GetInt32()!=1)throw new InvalidDataException("Unsupported catalogue.");
    if(flags.Contains("--download-ffmpeg")&&catalogue.RootElement.GetProperty("ffmpeg").ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Pinned FFmpeg metadata is incomplete. No download attempted; select installed FFmpeg or WAV-only.");
    using var http=new HttpClient(){Timeout=Timeout.InfiniteTimeSpan};using var cancel=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancel.Cancel();};
    var downloader=new PinnedDownloads(http);var provenance=new List<object>();
    async Task<string> Runtime(string engine,string option){if(options.TryGetValue(option,out var existing)){existing=SafeFiles.Full(existing);PinnedDownloads.RequirePe(existing,arch);provenance.Add(new{kind=engine,path=existing,verified_pin=false,sha256=SafeFiles.Hash(existing)});return existing;}
        var pin=catalogue.RootElement.GetProperty("runtimes").EnumerateArray().Single(x=>x.GetProperty("engine").GetString()==engine&&x.GetProperty("architecture").GetString()==arch);var artifact=DownloadPin.From(pin);var archive=await downloader.AcquireAsync(artifact,cache,flags.Contains("--offline"),cancel.Token);var dir=Path.Combine(destination,engine);PinnedDownloads.Extract(archive,dir);var exe=Directory.EnumerateFiles(dir,engine=="whisper"?"whisper-cli.exe":"llama-cli.exe",SearchOption.AllDirectories).Single();PinnedDownloads.RequirePe(exe,arch);provenance.Add(new{kind=engine,path=exe,verified_pin=true,version=pin.GetProperty("version").GetString(),artifact});return exe;}
    async Task<string> Model(string file,string option){var pin=DownloadPin.From(catalogue.RootElement.GetProperty("models").EnumerateArray().Single(x=>x.GetProperty("file").GetString()==file));string path;if(options.TryGetValue(option,out var existing)){path=SafeFiles.Full(existing);await PinnedDownloads.VerifyAsync(path,pin,cancel.Token);}else path=await downloader.AcquireAsync(pin,cache,flags.Contains("--offline"),cancel.Token);provenance.Add(new{kind="model",path,verified_pin=true,artifact=pin});return path;}
    var whisper=await Runtime("whisper","--existing-whisper");var llama=await Runtime("llama","--existing-llama");
    var whisperModel=await Model(asr=="small"?"ggml-small.bin":"ggml-large-v3-turbo.bin","--existing-asr-model");var notesModel=await Model(notes=="9b"?"Qwen3.5-9B-Q4_K_M.gguf":"Qwen3.5-27B-Q4_K_M.gguf","--existing-notes-model");
    var ffmpeg=options.GetValueOrDefault("--ffmpeg")??"";if(ffmpeg!=""){ffmpeg=SafeFiles.Full(ffmpeg);PinnedDownloads.RequirePe(ffmpeg,arch);provenance.Add(new{kind="ffmpeg",path=ffmpeg,verified_pin=false,sha256=SafeFiles.Hash(ffmpeg)});}
    if(flags.Contains("--download-ffmpeg")){
        var pin=catalogue.RootElement.GetProperty("ffmpeg");var artifact=DownloadPin.From(pin);var archive=await downloader.AcquireAsync(artifact,cache,flags.Contains("--offline"),cancel.Token);
        var directory=Path.Combine(destination,"ffmpeg");PinnedDownloads.Extract(archive,directory);ffmpeg=Directory.EnumerateFiles(directory,"ffmpeg.exe",SearchOption.AllDirectories).Single();PinnedDownloads.RequirePe(ffmpeg,"x64");
        provenance.Add(new{kind="ffmpeg",path=ffmpeg,verified_pin=true,version=pin.GetProperty("version").GetString(),artifact});
    }
    var settings=new AppSettings{OutputDirectory=Path.Combine(destination,"exports"),Models=new(){WhisperProfile=asr=="small"?"small":"large-v3-turbo",NotesProfile=notes=="9b"?"Qwen3.5 9B GGUF":"Qwen3.5 27B GGUF",WhisperExecutable=whisper,WhisperModel=whisperModel,LlamaExecutable=llama,LlamaModel=notesModel,FfmpegExecutable=ffmpeg,Compute="CPU"}};
    SafeFiles.Atomic(Path.Combine(destination,"settings.json"),JsonSerializer.Serialize(settings,JsonFormat.Options),false);
    SafeFiles.Atomic(Path.Combine(destination,"provision.json"),JsonSerializer.Serialize(new{schema_version=1,architecture=arch,compute="CPU",wav_only=ffmpeg=="",full_pipeline_ready=ffmpeg!="",actual_inference_tested=false,provenance},JsonFormat.Options),false);
    SafeFiles.Atomic(Path.Combine(destination,"test-wav.ps1"),"param([Parameter(Mandatory=$true)][string]$AudioFile)\n$ErrorActionPreference='Stop'\n$s=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'settings.json') -Raw | ConvertFrom-Json\nif([IO.Path]::GetExtension($AudioFile) -ne '.wav'){throw 'This smoke test accepts finished 16 kHz mono PCM WAV only.'}\n& $s.Models.WhisperExecutable -m $s.Models.WhisperModel -f $AudioFile -l ko -ng -otxt -of (Join-Path $PSScriptRoot 'wav-smoke')\nif($LASTEXITCODE -ne 0){throw \"whisper exited $LASTEXITCODE\"}\nWrite-Output 'ASR runtime exited successfully. Inspect wav-smoke.txt; notes/app pipeline were not tested.'\n",false);
    Console.WriteLine("Provisioned CPU preset: "+Path.Combine(destination,"settings.json"));if(ffmpeg=="")Console.WriteLine("WAV-ONLY: use test-wav.ps1. The current app pipeline still requires FFmpeg; generated settings are not full-pipeline ready.");
    return 0;
}catch(Exception e){Console.Error.WriteLine(e.Message+"\nCompleted verified cache entries are retained. Failed partial downloads are removed. A partial installation is preserved for diagnosis; choose a new destination after resolving the error.");return 1;}
