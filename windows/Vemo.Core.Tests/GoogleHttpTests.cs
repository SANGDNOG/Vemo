using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class GoogleHttpTests
{
    const string MetadataQuery = "?fields=" + "id%2Cetag%2Ctitle%2CmimeType%2Cparents%28id%29%2Cproperties%28key%2Cvalue%2Cvisibility%29%2Clabels%28trashed%29%2CfileSize";

    [Theory]
    [InlineData("GET", "/drive/v2/files/file-1", MetadataQuery)]
    [InlineData("PUT", "/upload/drive/v2/files/file-1", "?uploadType=media")]
    public async Task OnlyRequiredV2RequestsPassEndpointValidation(string method, string path, string query)
    {
        using var transport = new GoogleHttpTransport(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://www.googleapis.com" + path + query);
        // A cancelled send reaches no network; invalid endpoints instead throw invalid_endpoint first.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(request, 8 * 1024 * 1024, (_, _) => ValueTask.CompletedTask, cancellation.Token));
    }

    [Theory]
    [InlineData("GET", "/drive/v2/files/file-1", "")]
    [InlineData("GET", "/drive/v2/files/file-1", "?fields=id%2Cetag")]
    [InlineData("GET", "/drive/v2/files/file-1", MetadataQuery + "&alt=media")]
    [InlineData("GET", "/drive/v2/files/file-1", MetadataQuery + "&fields=id")]
    [InlineData("GET", "/drive/v2/files", MetadataQuery)]
    [InlineData("GET", "/drive/v2/files/", MetadataQuery)]
    [InlineData("GET", "/drive/v2/files/file-1/permissions", MetadataQuery)]
    [InlineData("GET", "/drive/v2/files/file%2F1", MetadataQuery)]
    [InlineData("GET", "/drive/v2/files/file.1", MetadataQuery)]
    [InlineData("POST", "/drive/v2/files/file-1", MetadataQuery)]
    [InlineData("PUT", "/drive/v2/files/file-1", "?uploadType=media")]
    [InlineData("PATCH", "/upload/drive/v2/files/file-1", "?uploadType=media")]
    [InlineData("PUT", "/upload/drive/v2/files/file-1", "")]
    [InlineData("PUT", "/upload/drive/v2/files/file-1", "?uploadType=multipart")]
    [InlineData("PUT", "/upload/drive/v2/files/file-1", "?uploadType=media&fields=id")]
    [InlineData("PUT", "/upload/drive/v2/files/file-1", "?uploadType=media&uploadType=media")]
    [InlineData("PUT", "/upload/drive/v2/files/file-1/permissions", "?uploadType=media")]
    [InlineData("PUT", "/upload/drive/v2/files/file%2F1", "?uploadType=media")]
    [InlineData("PATCH", "/upload/drive/v3/files/file-1", "?uploadType=media")]
    public async Task UnneededV2PathsMethodsAndQueriesAreRejectedBeforeNetwork(string method, string path, string query)
    {
        using var transport = new GoogleHttpTransport(); using var request = new HttpRequestMessage(new HttpMethod(method), "https://www.googleapis.com" + path + query);
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => transport.SendAsync(request, 8 * 1024 * 1024, (_, _) => ValueTask.CompletedTask, default));
        Assert.Equal("invalid_endpoint", error.Code);
    }
}
