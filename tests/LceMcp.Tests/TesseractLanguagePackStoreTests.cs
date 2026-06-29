using System.Net;

namespace LceMcp.Tests;

public sealed class TesseractLanguagePackStoreTests
{
    [Fact]
    public void DownloadsPinnedOfficialModelOnceAndReusesCache()
    {
        using var temp = TempWorkspace.Create();
        var handler = new StubHttpHandler(new byte[2_048]);
        using var client = new HttpClient(handler);
        var store = new TesseractLanguagePackStore(temp.Paths, allowDownloads: true, client);

        var first = store.EnsureLanguage("ENG");
        var second = store.EnsureLanguage("eng");

        Assert.Equal("eng", first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains(
            TesseractLanguagePackStore.TessdataCommit,
            handler.LastRequestUri.AbsoluteUri);
        Assert.EndsWith("/eng.traineddata", handler.LastRequestUri.AbsoluteUri);
        Assert.Equal(["eng"], store.ListCachedModels());
    }

    [Fact]
    public void MissingCachedModelFailsWithoutNetworkWhenDownloadsAreDisabled()
    {
        using var temp = TempWorkspace.Create();
        var store = new TesseractLanguagePackStore(temp.Paths, allowDownloads: false);

        var error = Assert.Throws<AttachmentExtractionException>(
            () => store.EnsureLanguage("eng"));

        Assert.Equal("ocr_language_pack_missing", error.ErrorCode);
        Assert.False(Directory.Exists(temp.Paths.TessdataDirectory));
    }

    private sealed class StubHttpHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }
}
