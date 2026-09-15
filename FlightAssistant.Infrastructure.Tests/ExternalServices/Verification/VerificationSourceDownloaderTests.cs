using System.Net;
using System.Text;
using FlightAssistant.Infrastructure.ExternalServices.Verification;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// Direct unit tests for <see cref="VerificationSourceDownloader.DownloadAsync{T}"/> -- the
/// download/error-mapping pipeline fix round 1 (item 2) extracted out of
/// <c>OurAirportsVerificationSource</c>/<c>MledozeCountriesVerificationSource</c> so it exists in
/// one place. Tested here directly, with a real caller-owned <see cref="CancellationToken"/>
/// (rather than through <see cref="VerificationDatasetCache{T}"/>, which -- per fix round 1, item 7
/// -- always invokes the downloader with <see cref="CancellationToken.None"/>): this is the one
/// place the caller-cancellation-vs-deadline distinction (item 1) is actually exercised end to end.
/// Internal type, visible here via <c>InternalsVisibleTo("FlightAssistant.Infrastructure.Tests")</c>.
/// </summary>
public class VerificationSourceDownloaderTests
{
    private const string ClientName = "test-client";
    private const string Url = "https://example.test/dataset";
    private const string SourceName = "test-source";

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverCompletingStream()) });
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers with headers immediately, but any read of the body throws
    /// <see cref="IOException"/> -- simulates a connection reset mid-stream (fix round 2, item 9).</summary>
    private sealed class ThrowingMidReadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingMidReadStream()) });
    }

    private sealed class ThrowingMidReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset mid-body");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsTheParsedResult_OnSuccess()
    {
        var factory = new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, "hello"));

        var result = await VerificationSourceDownloader.DownloadAsync(
            factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(5), ReadAllTextAsync, default);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsASourceError_OnANonSuccessStatus()
    {
        var factory = new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.InternalServerError, "boom"));

        var ex = await Assert.ThrowsAsync<VerificationSourceFetchException>(() =>
            VerificationSourceDownloader.DownloadAsync(
                factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(5), ReadAllTextAsync, default));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsASourceError_OnAHandlerLevelNetworkFailure()
    {
        var factory = new FakeHttpClientFactory(new ThrowingHandler(new HttpRequestException("connection reset")));

        await Assert.ThrowsAsync<VerificationSourceFetchException>(() =>
            VerificationSourceDownloader.DownloadAsync(
                factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(5), ReadAllTextAsync, default));
    }

    [Fact]
    public async Task DownloadAsync_ThrowsASourceError_WhenTheDeadlineElapses_AndTheCallerDidNotCancel()
    {
        var factory = new FakeHttpClientFactory(new NeverRespondingHandler());

        var task = VerificationSourceDownloader.DownloadAsync(
            factory, ClientName, Url, SourceName, TimeSpan.FromMilliseconds(50), ReadAllTextAsync, default);

        await Assert.ThrowsAsync<VerificationSourceFetchException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DownloadAsync_ThrowsASourceError_WhenTheBodyStalls()
    {
        // Fix round 1, item 1: headers return (200 OK) but the body never completes -- the deadline
        // must bound the parse phase too, not just time-to-headers.
        var factory = new FakeHttpClientFactory(new StalledBodyHandler());

        var task = VerificationSourceDownloader.DownloadAsync(
            factory, ClientName, Url, SourceName, TimeSpan.FromMilliseconds(50), ReadAllTextAsync, default);

        await Assert.ThrowsAsync<VerificationSourceFetchException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DownloadAsync_PropagatesCancellation_WhenTheCallerCancelsBeforeTheDeadline()
    {
        // Distinguishes caller cancellation from "our own deadline elapsed": a long deadline (30s)
        // that the test never actually waits out, cancelled by the caller almost immediately. This
        // is the test the fix-round-1 review's originally-suggested break-proof ("remove the `when
        // (!ct.IsCancellationRequested)` filter") targets directly.
        var factory = new FakeHttpClientFactory(new NeverRespondingHandler());
        using var cts = new CancellationTokenSource();

        var task = VerificationSourceDownloader.DownloadAsync(
            factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(30), ReadAllTextAsync, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DownloadAsync_ThrowsASourceError_WithDownloadWording_WhenTheBodyThrowsIOExceptionMidRead()
    {
        // Fix round 2, item 9: an IOException reading the body (a connection reset mid-stream) is a
        // download failure, not a parse failure -- distinguished by wording, since both surface as
        // VerificationSourceFetchException.IsError either way and only the message tells them apart.
        var factory = new FakeHttpClientFactory(new ThrowingMidReadHandler());

        var ex = await Assert.ThrowsAsync<VerificationSourceFetchException>(() =>
            VerificationSourceDownloader.DownloadAsync(
                factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(5), ReadAllTextAsync, default));

        Assert.Contains("Failed to download", ex.Message);
        Assert.DoesNotContain("Could not parse", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_PassesThroughAVerificationSourceFetchException_FromParse_Unwrapped()
    {
        // A source's own "zero usable rows" check (fix round 1, item 6) already throws the right
        // shape -- the downloader must not wrap it a second time, which would bury the real message.
        var factory = new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, "irrelevant"));
        Task<string> Parse(Stream stream, CancellationToken ct) =>
            throw new VerificationSourceFetchException("the dataset had no usable rows");

        var ex = await Assert.ThrowsAsync<VerificationSourceFetchException>(() =>
            VerificationSourceDownloader.DownloadAsync(factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(5), Parse, default));

        Assert.Equal("the dataset had no usable rows", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_WrapsAnUnexpectedParseException()
    {
        var factory = new FakeHttpClientFactory(new StaticHandler(HttpStatusCode.OK, "irrelevant"));
        Task<string> Parse(Stream stream, CancellationToken ct) => throw new FormatException("bad shape");

        var ex = await Assert.ThrowsAsync<VerificationSourceFetchException>(() =>
            VerificationSourceDownloader.DownloadAsync(factory, ClientName, Url, SourceName, TimeSpan.FromSeconds(5), Parse, default));

        Assert.Contains("bad shape", ex.Message);
    }
}
