namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// The download-and-parse pipeline shared by every verification source (#367 task 3 fix round
    /// 1): send, map a non-success status/network failure/timeout to a <see cref="VerificationSourceFetchException"/>,
    /// then hand the response body to the source's own <c>parse</c> delegate under the same
    /// timeout. <see cref="OurAirportsVerificationSource"/> and <see cref="MledozeCountriesVerificationSource"/>
    /// differ only in their client name, URL and parse step -- everything else (the error mapping
    /// item 2 of the fix-round review found duplicated verbatim) lives here once.
    /// </summary>
    internal static class VerificationSourceDownloader
    {
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
        /// unparseable content -- becomes a <see cref="VerificationSourceFetchException"/>. When
        /// <paramref name="ct"/> itself is cancelled, the resulting <see cref="OperationCanceledException"/>
        /// is left to propagate: that is caller cancellation, not a source error.
        /// </para>
        ///
        /// <para>
        /// A <see cref="VerificationSourceFetchException"/> thrown directly by <paramref name="parse"/>
        /// (e.g. "the dataset had zero usable rows" -- fix round 1, item 6) passes through unwrapped.
        /// An <see cref="IOException"/> or <see cref="HttpRequestException"/> reading the body (fix
        /// round 2, item 9) -- a connection reset mid-stream, for instance -- is worded as a
        /// <b>download</b> failure ("Failed to download..."), distinct from every other exception
        /// from <paramref name="parse"/>, which is worded as a <b>parse</b> failure ("Could not
        /// parse..."): the former means the bytes never fully arrived, the latter means they did
        /// but were not valid CSV/JSON, and conflating the two previously mislabelled a network
        /// failure as a content problem.
        /// </para>
        /// </summary>
        public static async Task<T> DownloadAsync<T>(
            IHttpClientFactory httpClientFactory,
            string clientName,
            string url,
            string sourceName,
            TimeSpan timeout,
            Func<Stream, CancellationToken, Task<T>> parse,
            CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var linkedCt = timeoutCts.Token;

            var http = httpClientFactory.CreateClient(clientName);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new VerificationSourceFetchException($"Timed out downloading the {sourceName} dataset.");
            }
            catch (HttpRequestException ex)
            {
                throw new VerificationSourceFetchException($"Could not reach {sourceName}: {ex.Message}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Any non-success -- including 429 and a 404 on the dataset URL itself -- is a
                    // source error, never treated as "no data" (that is what an empty index means).
                    throw new VerificationSourceFetchException(
                        $"{sourceName} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                Stream stream;
                try
                {
                    stream = await response.Content.ReadAsStreamAsync(linkedCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new VerificationSourceFetchException($"Timed out downloading the {sourceName} dataset.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fix round 2, item 9: this previously mapped only cancellation -- any other
                    // failure obtaining the stream (e.g. a reset connection) escaped unhandled.
                    throw new VerificationSourceFetchException($"Failed to download the {sourceName} dataset: {ex.Message}", ex);
                }

                await using (stream.ConfigureAwait(false))
                {
                    try
                    {
                        // `parse` is a plain delegate, not necessarily `async` -- calling it can
                        // itself throw synchronously (as several tests' inline `parse` functions
                        // do), so it stays inside this try, same as before.
                        var parseTask = parse(stream, linkedCt);

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
                    catch (VerificationSourceFetchException)
                    {
                        // Already the right shape (e.g. the "zero usable rows" check) -- pass through
                        // rather than wrapping it a second time.
                        throw;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new VerificationSourceFetchException($"Timed out downloading the {sourceName} dataset.");
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        // Fix round 2, item 9: an I/O failure reading the body (e.g. a connection
                        // reset mid-stream) is a download failure, not a parse failure -- it has
                        // nothing to do with whether the bytes actually received were valid
                        // CSV/JSON. Checked before the catch-all below so it isn't mislabelled
                        // "Could not parse" the way it was before this fix.
                        throw new VerificationSourceFetchException($"Failed to download the {sourceName} dataset: {ex.Message}", ex);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new VerificationSourceFetchException($"Could not parse the {sourceName} dataset: {ex.Message}", ex);
                    }
                }
            }
        }
    }
}
