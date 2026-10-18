using System.Reflection;
using System.Text;
namespace Vemo.Core;

/// Bundles only the installed desktop public-client identity, never account credentials.
public static class GoogleBundledClientConfiguration
{
    public const string ResourceName = "Vemo.Google.InstalledClient.json";
    static readonly HashSet<string> InstalledKeys = new(StringComparer.Ordinal)
    { "client_id", "client_secret", "project_id", "auth_uri", "token_uri", "auth_provider_x509_cert_url", "redirect_uris" };

    /// Validates using the normal installed-client parser and strips endpoints and unrelated metadata.
    /// Used by the release build too; exceptions never contain input values.
    public static string NormalizeInstalledJson(string json)
    {
        try
        {
            var config = GoogleOAuthConfiguration.ParseInstalledJson(json);
            using var doc = JsonFormat.Strict(json); GoogleWire.Keys(doc.RootElement, "installed");
            if (doc.RootElement.GetProperty("installed").EnumerateObject().Any(p => !InstalledKeys.Contains(p.Name))) throw new GoogleConnectionException("invalid_configuration");
            return config.Serialize();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new GoogleConnectionException("invalid_configuration"); }
    }
    public static string? ReadFromAssembly(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName); if (stream == null) return null;
        try
        {
            using var bytes = new MemoryStream(); var buffer = new byte[4096]; int n;
            while ((n = stream.Read(buffer)) != 0) { if (bytes.Length + n > 65536) throw new GoogleConnectionException("invalid_configuration"); bytes.Write(buffer, 0, n); }
            return NormalizeInstalledJson(new UTF8Encoding(false, true).GetString(bytes.ToArray()));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new GoogleConnectionException("invalid_configuration"); }
    }
    public static string? ReadFromApplication()
    {
        var entry = Assembly.GetEntryAssembly();
        var json = entry == null ? null : ReadFromAssembly(entry); if (json != null) return json;
        // Headless UI tests load Vemo.App under the test runner rather than as the entry assembly.
        var app = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Vemo.App");
        return app == null || app == entry ? null : ReadFromAssembly(app);
    }
}
