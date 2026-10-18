using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Vemo.Core;

public sealed class GoogleOAuthLoopback : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly GoogleOAuthPkce pkce;
    public Uri RedirectUri { get; }
    public GoogleOAuthLoopback(GoogleOAuthPkce pkce)
    {
        this.pkce = pkce; listener.Start(4);
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/oauth/callback");
    }
    public async Task<string> WaitForCodeAsync(CancellationToken ct = default)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(5));
            using var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
            try
            {
                while (header.Count < 12288)
                {
                    if (await stream.ReadAsync(one, bounded.Token) == 0) break;
                    header.Add(one[0]); if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
                var hosts = lines.Skip(1).Where(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (header.Count >= 12288 || first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1" || hosts.Length != 1 || hosts[0][5..].Trim() != RedirectUri.Authority)
                { await Reply(stream, false, bounded.Token); continue; }
                string code;
                try { code = pkce.ParseCallback(first[1]); }
                catch (GoogleConnectionException e) when (e.Code != "access_denied") { await Reply(stream, false, bounded.Token); continue; }
                await Reply(stream, true, bounded.Token); return code;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { ct.ThrowIfCancellationRequested(); }
        }
    }
    static Task Reply(NetworkStream stream, bool ok, CancellationToken ct) => stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\nCache-Control: no-store\r\n\r\nOK"), ct).AsTask();
    public void Dispose() => listener.Stop();
}
