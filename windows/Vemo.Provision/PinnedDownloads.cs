using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Vemo.Core;
namespace Vemo.Provision;
public sealed record DownloadPin(string File,string Url,long Size,string Sha256) {
    public static DownloadPin From(JsonElement value)=>new(value.GetProperty("file").GetString()!,value.GetProperty("url").GetString()!,value.GetProperty("size").GetInt64(),value.GetProperty("sha256").GetString()!);
    public void Validate(){SafeFiles.Name(File);if(Size<=0||!System.Text.RegularExpressions.Regex.IsMatch(Sha256,"^[a-f0-9]{64}$"))throw new InvalidDataException("Invalid artifact pin.");if(!Uri.TryCreate(Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Host is not ("github.com" or "huggingface.co"))throw new InvalidDataException("Artifact must use an approved upstream HTTPS URL.");}
}
public sealed class PinnedDownloads(HttpClient http) {
    public static async Task VerifyAsync(string path,DownloadPin pin,CancellationToken ct){pin.Validate();SafeFiles.NoLinks(path);if(new FileInfo(path).Length!=pin.Size)throw new InvalidDataException("Pinned size mismatch: "+path);await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));if(hash!=pin.Sha256)throw new InvalidDataException("Pinned SHA-256 mismatch: "+path);}
    public async Task<string> AcquireAsync(DownloadPin pin,string cache,bool offline,CancellationToken ct){
        pin.Validate();SafeFiles.NoLinks(cache);Directory.CreateDirectory(cache);var dest=Path.Combine(cache,pin.File);SafeFiles.NoLinks(dest);
        if(System.IO.File.Exists(dest)){await VerifyAsync(dest,pin,ct);Console.WriteLine("Verified existing "+pin.File);return dest;}
        if(offline)throw new FileNotFoundException("Offline cache is missing "+pin.File);
        var partial=dest+".partial-"+Guid.NewGuid();SafeFiles.NoLinks(partial);
        try {
            Console.WriteLine($"Downloading {pin.File} ({pin.Size:N0} bytes)…");
            using var response=await http.GetAsync(pin.Url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            var final=response.RequestMessage?.RequestUri;if(final?.Scheme!="https")throw new IOException("Download redirected away from HTTPS.");
            if(response.Content.Headers.ContentLength is long length&&length!=pin.Size)throw new InvalidDataException("Upstream size differs from pinned metadata.");
            await using(var input=await response.Content.ReadAsStreamAsync(ct))await using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true)){
                var buffer=new byte[1024*1024];long count=0;int n;while((n=await input.ReadAsync(buffer,ct))>0){count+=n;if(count>pin.Size)throw new InvalidDataException("Download exceeds pinned size.");await output.WriteAsync(buffer.AsMemory(0,n),ct);}await output.FlushAsync(ct);output.Flush(true);
            }
            await VerifyAsync(partial,pin,ct);SafeFiles.NoLinks(dest);System.IO.File.Move(partial,dest,false);Console.WriteLine("SHA-256 verified "+pin.File);return dest;
        }finally{if(System.IO.File.Exists(partial))System.IO.File.Delete(partial);}
    }
    public static void Extract(string archive,string destination){
        SafeFiles.NoLinks(archive);SafeFiles.NoLinks(destination);if(Directory.Exists(destination))throw new IOException("Runtime extraction destination already exists.");
        var staging=destination+".staging-"+Guid.NewGuid();Directory.CreateDirectory(staging);
        try {
            using var zip=ZipFile.OpenRead(archive);if(zip.Entries.Count>10000)throw new InvalidDataException("Runtime archive has too many files.");long total=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in zip.Entries){
                var name=entry.FullName.Replace('\\','/');var pieces=name.TrimEnd('/').Split('/');if(name.StartsWith('/')||pieces.Length==0||pieces.Any(p=>p is "" or "." or ".."))throw new InvalidDataException("Unsafe runtime archive path.");foreach(var piece in pieces)SafeFiles.Name(piece);
                if(!seen.Add(string.Join('/',pieces)))throw new InvalidDataException("Duplicate runtime archive path.");var mode=(entry.ExternalAttributes>>16)&0xf000;if(mode is not (0 or 0x8000 or 0x4000)||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Runtime archive contains a link or unsupported entry.");
                total+=entry.Length;if(total>2L*1024*1024*1024)throw new InvalidDataException("Runtime extraction exceeds 2 GiB.");
                var target=Path.Combine([staging,..pieces]);if(!SafeFiles.Within(staging,target))throw new InvalidDataException("Archive traversal.");
                if(name.EndsWith('/'))Directory.CreateDirectory(target);else{Directory.CreateDirectory(Path.GetDirectoryName(target)!);entry.ExtractToFile(target,false);}
            }
            Directory.Move(staging,destination);
        }finally{if(Directory.Exists(staging))SafeFiles.DeleteTree(staging);}
    }
    public static void RequirePe(string file,string arch){SafeFiles.NoLinks(file);using var reader=new BinaryReader(System.IO.File.OpenRead(file));if(reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException("Selected executable is not a Windows native PE file: "+file);reader.BaseStream.Position=60;var offset=reader.ReadInt32();if(offset<64||offset>reader.BaseStream.Length-6)throw new InvalidDataException("Invalid PE header.");reader.BaseStream.Position=offset;if(reader.ReadUInt32()!=0x4550||reader.ReadUInt16()!=(arch=="x64"?0x8664:0xaa64))throw new InvalidDataException("Executable architecture does not match "+arch+": "+file);}
}
