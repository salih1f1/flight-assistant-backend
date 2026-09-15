using System.Net;

namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// The download-and-parse pipeline shared by every verification source (#367 task 3 fix round
    /// 1): send, map a non-success status/network failure/timeout to a <see cref="VerificationSourceFetchException"/>,
    /// then hand the response body to the source's own <c>parse</c> delegate under the same
    /// timeout. <see cref="OurAirportsVerificationSource"/> and <see cref="MledozeCountriesVerificationSource"/>
    /// differ only in their client name, URL, size cap and parse step -- everything else (the error
    /// mapping item 2 of the fix-round review found duplicated verbatim) lives here once.
    ///
    /// <para>
    /// <b>Error text (final fix wave, C1).</b> A source error's message ends up in
    /// <c>verification_requests.error</c>, which admins see in every environment, Production
    /// included. So it is one of the fixed categories below plus facts this code controls (the
    /// source name, an HTTP status code, the size cap), <b>never</b> an upstream exception's
    /// message: a proxy or socket error can name an internal address, and a CsvHelper or JSON error
    /// can quote raw record content. The original exception stays attached as the
    /// <see cref="Exception.InnerException"/>, and each source logs the whole exception with its
    /// name.
    /// </para>
    ///
    /// <para>
    /// <b>Size cap (final fix wave, C5).</b> Each source passes the most bytes it will read. A
    /// declared <c>Content-Length</c> over it fails before any of the body is read, as
    /// <c>GitHubCiRunProvider</c> checks its archive; a body with no declared length, or a wrong one,
    /// is counted as it is read and fails as soon as it passes the cap. Unlike that provider's
    /// <c>ReadBoundedAsync</c>, the body is not buffered into memory first: OurAirports parses its
    /// CSV as it streams, and buffering up to the cap would defeat that.
    /// </para>
    /// </summary>
    internal static class VerificationSourceDownloader
    {
        /// <summary>The deadline elapsed during the send, the body read or the parse.</summary>
        internal static string TimedOutError(string sourceName) => $"Timed out downloading the {sourceName} dataset";

        /// <summary>The dataset URL answered a non-success status.</summary>
        internal static string HttpStatusError(string sourceName, HttpStatusCode status) =>
            $"{sourceName} dataset download failed: HTTP {(int)status}";

        /// <summary>The send failed, or the body could not be read in full.</summary>
        internal static string NetworkError(string sourceName) => $"Failed to download the {sourceName} dataset (network error)";

        /// <summary>The body is larger than the source's download cap.</summary>
        internal static string TooLargeError(string sourceName, long maxBytes) =>
            $"The {sourceName} dataset is larger than the {maxBytes}-byte download limit";

        /// <summary>The body arrived but is not a dataset this source can read.</summary>
        internal static string ParseError(string sourceName) => $"Could not parse the {sourceName} dataset";

        /// <summary>The body parsed but yielded no row this source can index.</summary>
        internal static string NoUsableRowsError(string sourceName) => $"The {sourceName} dataset had no usable rows";

        /// <summary>
        /// Downloads <paramref name="url"/> via the named client <paramref name="clientName"/>
        /// (fetched fresh from <paramref name="httpClientFactory"/> for this call -- see the "singleton
        /// holding IHttpClientFactory, not a captured HttpClient" note on each source) and hands the
        /// response body to <paramref name="parse"/>.
        ///
        /// <para>
        /// <b>Timeout covers send AND parse (fix round 1, item 1).</b> A single
        /// <see cref="CancellationTokenSource"/>, linked to <paramref name="ct"/> and cancelled after
        /// <paramref name="timeout"/>, bounds both the HTTP send (headers only --
        /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, since these datasets are large) and
        /// the subsequent body read/parse. Before this fix, <c>HttpClient.Timeout</c> covered only
        /// time-to-headers: a server that sent headers and then stalled the body blocked the caller
        /// forever, because parsing read the stream under nothing but the caller's own token.
        /// </para>
        ///
        /// <para>
        /// <paramref name="parse"/> is additionally raced via <see cref="Task.WaitAsync(CancellationToken)"/>
        /// against the same linked token -- belt-and-suspenders against a parser (or a pathological
        /// stream) that does not itself observe a per-read cancellation token promptly. When the
        /// linked token fires and <paramref name="ct"/> itself is <b>not</b> the reason (i.e. the
        /// deadline elapsed), every failure mode -- timeout, non-success status, network failure,
        /// oversized or unparseable content -- becomes a <see cref="VerificationSourceFetchException"/>.
        /// When <paramref name="ct"/> itself is cancelled, the resulting <see cref="OperationCanceledException"/>
        /// is left to propagate: that is caller cancellation, not a source error.
        /// </para>
        ///
        /// <para>
        /// A <see cref="VerificationSourceFetchException"/> thrown directly by <paramref name="parse"/>
        /// (e.g. <see cref="NoUsableRowsError"/> -- fix round 1, item 6) passes through unwrapped.
        /// An <see cref="IOException"/> or <see cref="HttpRequestException"/> reading the body (fix
        /// round 2, item 9) -- a connection reset mid-stream, for instance -- is a
        /// <see cref="NetworkError"/>, distinct from every other exception from
        /// <paramref name="parse"/>, which is a <see cref="ParseError"/>: the former means the bytes
        /// never fully arrived, the latter means they did but were not valid CSV/JSON.
        /// </para>
        /// </summary>
        /// <param name="maxBytes">The most body bytes to read; a larger body is a
        /// <see cref="TooLargeError"/>.</param>
        public static async Task<T> DownloadAsync<T>(
            IHttpClientFactory httpClientFactory,
            string clientName,
            string url,
            string sourceName,
            TimeSpan timeout,
            long maxBytes,
            Func<Stream, CancellationToken, Task<T>> parse,
            CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var linkedCt = timeoutCts.Token;

            var http = httpClientFactory.CreateClient(clientName);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new VerificationSourceFetchException(TimedOutError(sourceName), ex);
            }
            catch (HttpRequestException ex)
            {
                throw new VerificationSourceFetchException(NetworkError(sourceName), ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Any non-success -- including 429 and a 404 on the dataset URL itself -- is a
                    // source error, never treated as "no data" (that is what an empty index means).
                    // The reason phrase is upstream text, so only the numeric code is kept.
                    throw new VerificationSourceFetchException(HttpStatusError(sourceName, response.StatusCode));
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                    throw new VerificationSourceFetchException(TooLargeError(sourceName, maxBytes));

                Stream stream;
                try
                {
                    stream = await response.Content.ReadAsStreamAsync(linkedCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new VerificationSourceFetchException(TimedOutError(sourceName), ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fix round 2, item 9: this previously mapped only cancellation -- any other
                    // failure obtaining the stream (e.g. a reset connection) escaped unhandled.
                    throw new VerificationSourceFetchException(NetworkError(sourceName), ex);
                }

                var body = new LimitedReadStream(stream, maxBytes);
                await using (body.ConfigureAwait(false))
                {
                    try
                    {
                        // `parse` is a plain delegate, not necessarily `async` -- calling it can
                        // itself throw synchronously (as several tests' inline `parse` functions
                        // do), so it stays inside this try, same as before.
                        var parseTask = parse(body, linkedCt);

                        // Fix round 2, item 3b: if the WaitAsync race below abandons `parseTask`
                        // (the deadline wins), it keeps running in the background and nobody ever
                        // awaits it directly again -- an eventual fault on it would otherwise raise
                        // TaskScheduler.UnobservedTaskException once GC'd. Attaching this
                        // unconditionally is a no-op on the normal (non-abandoned) path: a
                        // continuation on an already-successful task with OnlyOnFaulted simply
                        // never runs.
                        _ = parseTask.ContinueWith(
                            completed => { if (completed.IsFaulted) _ = completed.Exception; },
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);

                        // WaitAsync is the safety net: it fires on the linked deadline even if
                        // `parse` (or a pathological stream underneath it) never itself observes
                        // per-read cancellation promptly.
                        return await parseTask.WaitAsync(linkedCt).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (body.LimitExceeded)
                    {
                        // First: a parser may wrap the stream's exception in one of its own.
                        throw new VerificationSourceFetchException(TooLargeError(sourceName, maxBytes), ex);
                    }
                    catch (VerificationSourceFetchException)
                    {
                        // Already the right shape (e.g. the "zero usable rows" check) -- pass through
                        // rather than wrapping it a second time.
                        throw;
                    }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        throw new VerificationSourceFetchException(TimedOutError(sourceName), ex);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        // Fix round 2, item 9: an I/O failure reading the body (e.g. a connection
                        // reset mid-stream) is a download failure, not a parse failure -- it has
                        // nothing to do with whether the bytes actually received were valid
                        // CSV/JSON. Checked before the catch-all below so it isn't mislabelled.
                        throw new VerificationSourceFetchException(NetworkError(sourceName), ex);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new VerificationSourceFetchException(ParseError(sourceName), ex);
                    }
                }
            }
        }

        /// <summary>
        /// A read-only pass-through over a response body that throws once more than
        /// <paramref name="maxBytes"/> bytes have been read, and remembers that it did
        /// (<see cref="LimitExceeded"/>), so the caller can tell the cap from a parse or network error
        /// however the parser wraps the exception. Disposing it disposes the body.
        /// </summary>
        private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
        {
            private long _bytesRead;

            public bool LimitExceeded { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _bytesRead;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

            public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            private int Count(int read)
            {
                _bytesRead += read;
                if (_bytesRead > maxBytes)
                {
                    LimitExceeded = true;
                    throw new IOException($"The response body is larger than the {maxBytes}-byte limit.");
                }

                return read;
            }
        }
    }
}
