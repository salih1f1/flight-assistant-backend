using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.GitHub
{
    /// <summary>
    /// Reads the CI workflow's runs and their <c>test-results</c> artifact from the GitHub REST API.
    ///
    /// <para>
    /// <b>Two clients, and only one carries the token.</b> <see cref="ApiClientName"/> sends the token
    /// and never follows redirects. An artifact download answers 302 with a short-lived signed
    /// blob-storage URL, which is fetched with <see cref="DownloadClientName"/>: that client has no
    /// credentials at all, so the token can't reach another host whatever the Location header says.
    /// </para>
    ///
    /// <para>
    /// Upstream failures map the way AeroAPI's do: a rejected token is our configuration (500), a
    /// rate limit is retryable (503), and anything else unexpected is a 502. The signed download URL
    /// is never logged; it grants access to the archive until it expires.
    /// </para>
    /// </summary>
    public sealed class GitHubCiRunProvider : ICiRunProvider
    {
        public const string ApiClientName = "GitHub";
        public const string DownloadClientName = "GitHubArtifactDownload";
        public const string TestResultsArtifactName = "test-results";
        public const string ReportFileName = "test-results.json";

        /// <summary>A real archive is about 250 KB: the JSON report plus four .trx files. Anything
        /// near this cap is not a CI report, and the archive is read into memory.</summary>
        public const long MaxArchiveBytes = 50L * 1024 * 1024;

        /// <summary>A real report is about 300 KB for ~900 tests. A zip entry's declared size can lie,
        /// so the decompressed read is capped as well.</summary>
        public const long MaxReportBytes = 20L * 1024 * 1024;

        private const int StatusCode404NotFound = 404;
        private const int StatusCode500InternalError = 500;
        private const int StatusCode502BadGateway = 502;
        private const int StatusCode503ServiceUnavailable = 503;

        private const string TemporarilyUnavailable = "CI results are temporarily unavailable. Please try again shortly.";
        private const string Unreadable = "This CI run's test report couldn't be read.";

        // GitHub error bodies are {"message": ..., "documentation_url": ...}; the message is enough.
        private const int MaxUpstreamMessage = 300;

        private static readonly JsonSerializerOptions GitHubJson =
            new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        // trx_to_json.py writes camelCase keys.
        private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _api;
        private readonly HttpClient _download;
        private readonly GitHubOptions _options;
        private readonly ILogger<GitHubCiRunProvider> _logger;

        public GitHubCiRunProvider(
            HttpClient api,
            HttpClient download,
            IOptions<GitHubOptions> options,
            ILogger<GitHubCiRunProvider> logger)
        {
            _api = api;
            _download = download;
            _options = options.Value;
            _logger = logger;
        }

        private string RepoPath =>
            $"repos/{Uri.EscapeDataString(_options.Owner)}/{Uri.EscapeDataString(_options.Repo)}";

        public async Task<IReadOnlyList<CiRun>> GetRunsAsync(string? branch, int limit, CancellationToken ct = default)
        {
            EnsureConfigured();

            var path = $"{RepoPath}/actions/workflows/{Uri.EscapeDataString(_options.Workflow)}/runs?per_page={limit}";
            if (!string.IsNullOrWhiteSpace(branch))
                path += $"&branch={Uri.EscapeDataString(branch)}";

            using var response = await SendAsync(_api, new Uri(path, UriKind.Relative), ct);

            // GitHub answers 404, not 403, for a private repository the token can't see, so this is as
            // likely to be the token as a wrong name. Either way it's our configuration.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var message = await ReadUpstreamMessageAsync(response, ct);
                _logger.LogError(
                    "GitHub returned 404 for CI workflow {Workflow} in {Owner}/{Repo}: {Message}",
                    _options.Workflow, _options.Owner, _options.Repo, message);
                throw new DomainException(
                    "CI results are unavailable.",
                    StatusCode500InternalError,
                    $"GitHub returned HTTP 404 for workflow '{_options.Workflow}' in {_options.Owner}/{_options.Repo}. " +
                    "Check GitHub:Owner, GitHub:Repo and GitHub:Workflow, and that the token can read that repository.");
            }

            await ThrowIfFailedAsync(response, ct);
            var page = await ReadGitHubJsonAsync<RunsPage>(response, ct);

            return (page.WorkflowRuns ?? [])
                .Select(run => new CiRun(
                    run.Id,
                    run.RunNumber,
                    run.RunAttempt,
                    run.DisplayTitle ?? "",
                    run.Event ?? "",
                    run.HeadBranch ?? "",
                    run.HeadSha ?? "",
                    run.Status ?? "",
                    run.Conclusion,
                    run.CreatedAt,
                    run.RunStartedAt,
                    run.UpdatedAt,
                    run.HtmlUrl ?? ""))
                .ToList();
        }

        public async Task<CiTestResultsArtifact?> FindTestResultsArtifactAsync(long runId, CancellationToken ct = default)
        {
            EnsureConfigured();

            var path = $"{RepoPath}/actions/runs/{runId}/artifacts?name={TestResultsArtifactName}&per_page=100";
            using var response = await SendAsync(_api, new Uri(path, UriKind.Relative), ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DomainException($"No CI run with id {runId}.", StatusCode404NotFound);

            await ThrowIfFailedAsync(response, ct);
            var page = await ReadGitHubJsonAsync<ArtifactsPage>(response, ct);

            // A re-run uploads another artifact under the same name, so the newest is the latest
            // attempt's. The name is checked here as well as by the query parameter, so a change to
            // GitHub's filter can't hand another artifact's archive back as a test report.
            return (page.Artifacts ?? [])
                .Where(a => a.Name == TestResultsArtifactName)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new CiTestResultsArtifact(a.Id, a.Expired, a.CreatedAt))
                .FirstOrDefault();
        }

        public async Task<CiTestReport> GetTestReportAsync(long artifactId, CancellationToken ct = default)
        {
            EnsureConfigured();

            var path = $"{RepoPath}/actions/artifacts/{artifactId}/zip";
            using var redirect = await SendAsync(_api, new Uri(path, UriKind.Relative), ct);

            // 410 is GitHub's answer for an artifact whose contents have expired.
            if (redirect.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new DomainException("This test report is no longer available on GitHub.", StatusCode404NotFound);

            await ThrowIfFailedAsync(redirect, ct);

            var status = (int)redirect.StatusCode;
            var location = redirect.Headers.Location;
            if (status is < 300 or >= 400
                || location is not { IsAbsoluteUri: true }
                || location.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogError(
                    "GitHub answered the download of artifact {ArtifactId} with HTTP {StatusCode} and no https redirect",
                    artifactId, status);
                throw new DomainException(
                    TemporarilyUnavailable,
                    StatusCode502BadGateway,
                    $"GitHub answered the artifact download with HTTP {status} instead of a redirect to an https download URL.");
            }

            using var download = await SendAsync(_download, location, ct);
            if (!download.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Downloading artifact {ArtifactId} returned HTTP {StatusCode}", artifactId, (int)download.StatusCode);
                throw new DomainException(
                    TemporarilyUnavailable,
                    StatusCode502BadGateway,
                    $"Downloading the artifact archive returned HTTP {(int)download.StatusCode}.");
            }

            if (download.Content.Headers.ContentLength > MaxArchiveBytes)
                throw TooLarge(artifactId, $"The artifact archive is over the {MaxArchiveBytes}-byte limit.");

            await using var body = await download.Content.ReadAsStreamAsync(ct);
            using var archive = await ReadBoundedAsync(body, MaxArchiveBytes, ct)
                ?? throw TooLarge(artifactId, $"The artifact archive is over the {MaxArchiveBytes}-byte limit.");

            ReportFile? file;
            try
            {
                using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
                var entry = zip.GetEntry(ReportFileName)
                    ?? throw TooLarge(artifactId, $"The {TestResultsArtifactName} artifact has no {ReportFileName}.");

                if (entry.Length > MaxReportBytes)
                    throw TooLarge(artifactId, $"{ReportFileName} is over the {MaxReportBytes}-byte limit.");

                await using var entryStream = entry.Open();
                using var json = await ReadBoundedAsync(entryStream, MaxReportBytes, ct)
                    ?? throw TooLarge(artifactId, $"{ReportFileName} is over the {MaxReportBytes}-byte limit.");

                file = await JsonSerializer.DeserializeAsync<ReportFile>(json, ReportJson, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                _logger.LogError(ex, "Artifact {ArtifactId} is not a readable test report", artifactId);
                throw new DomainException(
                    Unreadable,
                    ex,
                    StatusCode502BadGateway,
                    $"The artifact archive is not a readable zip containing {ReportFileName}.");
            }

            if (file is null)
                throw TooLarge(artifactId, $"{ReportFileName} is JSON null.");

            return new CiTestReport(
                file.Commit ?? "",
                file.Ref ?? "",
                file.Total,
                file.Passed,
                file.Failed,
                file.Skipped,
                file.DurationMs,
                (file.Suites ?? [])
                    .Select(suite => new CiTestSuite(
                        suite.Suite ?? "",
                        suite.Total,
                        suite.Passed,
                        suite.Failed,
                        suite.Skipped,
                        suite.DurationMs,
                        (suite.Tests ?? [])
                            .Select(test => new CiTestCase(
                                test.Name ?? "", test.Class ?? "", test.Outcome ?? "", test.DurationMs, test.Message ?? ""))
                            .ToList()))
                    .ToList());
        }

        private void EnsureConfigured()
        {
            if (!_options.IsConfigured)
                throw new DomainException(
                    "CI results aren't configured on this API.",
                    StatusCode503ServiceUnavailable,
                    "GitHub:Token is not set. Add a fine-grained token with Actions: read on the repository.");
        }

        private async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, CancellationToken ct)
        {
            try
            {
                return await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            // An HttpClient timeout is an OperationCanceledException too; only the caller's own
            // cancellation is let through.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // No URL in the log: the artifact download URL is a signed link.
                _logger.LogError(ex, "A GitHub CI request failed before any response");
                throw new DomainException(TemporarilyUnavailable, ex, StatusCode502BadGateway);
            }
        }

        private async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
        {
            var status = (int)response.StatusCode;
            if (status < 400)
                return;

            var message = await ReadUpstreamMessageAsync(response, ct);

            if (IsRateLimited(response, message))
            {
                _logger.LogWarning("GitHub rate-limited a CI request ({StatusCode}): {Message}", status, message);
                throw new DomainException(
                    "GitHub is rate-limiting CI result requests. Please try again shortly.",
                    StatusCode503ServiceUnavailable,
                    $"GitHub returned HTTP {status}: {message}");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError("GitHub rejected the CI token ({StatusCode}): {Message}", status, message);
                throw new DomainException(
                    "CI results are unavailable.",
                    StatusCode500InternalError,
                    $"GitHub returned HTTP {status}: {message}. Check that GitHub:Token is valid and has " +
                    $"Actions: read on {_options.Owner}/{_options.Repo}.");
            }

            _logger.LogError("GitHub returned an unexpected status for a CI request ({StatusCode}): {Message}", status, message);
            throw new DomainException(
                TemporarilyUnavailable,
                StatusCode502BadGateway,
                $"GitHub returned HTTP {status}: {message}");
        }

        /// <summary>GitHub's primary rate limit answers 403 or 429 with <c>x-ratelimit-remaining: 0</c>;
        /// its secondary limit answers 403 or 429 with <c>Retry-After</c> or a message saying so. A 403
        /// with none of those is a permissions problem.</summary>
        private static bool IsRateLimited(HttpResponseMessage response, string message) =>
            response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden
                && ((response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
                     && remaining.FirstOrDefault() == "0")
                    || response.Headers.RetryAfter is not null
                    || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)));

        private async Task<T> ReadGitHubJsonAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
        {
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync<T>(body, GitHubJson, ct)
                       ?? throw new JsonException("The body was JSON null.");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "GitHub returned an unparseable CI response ({StatusCode})", (int)response.StatusCode);
                throw new DomainException(
                    TemporarilyUnavailable,
                    StatusCode502BadGateway,
                    "GitHub returned a body that is not the expected JSON.");
            }
        }

        /// <summary>GitHub's error message, with the token redacted (GitHub doesn't echo it; this is
        /// defence in depth) and then cut short.</summary>
        private async Task<string> ReadUpstreamMessageAsync(HttpResponseMessage response, CancellationToken ct)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception)
            {
                return "(no body)";
            }

            var message = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("message", out var value)
                    && value.ValueKind == JsonValueKind.String)
                    message = value.GetString() ?? body;
            }
            catch (JsonException)
            {
                // Not JSON: keep the raw text.
            }

            if (!string.IsNullOrEmpty(_options.Token))
                message = message.Replace(_options.Token, "[REDACTED]", StringComparison.Ordinal);

            return message.Length > MaxUpstreamMessage ? message[..MaxUpstreamMessage] : message;
        }

        private DomainException TooLarge(long artifactId, string detail)
        {
            _logger.LogError("Artifact {ArtifactId} is not a readable test report: {Detail}", artifactId, detail);
            return new DomainException(Unreadable, StatusCode502BadGateway, detail);
        }

        /// <summary>Copies <paramref name="source"/> into memory, or returns null once it passes
        /// <paramref name="maxBytes"/>.</summary>
        private static async Task<MemoryStream?> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await source.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    await buffer.DisposeAsync();
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return buffer;
        }

        private sealed class RunsPage
        {
            public List<RunJson>? WorkflowRuns { get; set; }
        }

        private sealed class RunJson
        {
            public long Id { get; set; }
            public int RunNumber { get; set; }
            public int RunAttempt { get; set; }
            public string? DisplayTitle { get; set; }
            public string? Event { get; set; }
            public string? HeadBranch { get; set; }
            public string? HeadSha { get; set; }
            public string? Status { get; set; }
            public string? Conclusion { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset? RunStartedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string? HtmlUrl { get; set; }
        }

        private sealed class ArtifactsPage
        {
            public List<ArtifactJson>? Artifacts { get; set; }
        }

        private sealed class ArtifactJson
        {
            public long Id { get; set; }
            public string? Name { get; set; }
            public bool Expired { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
        }

        private sealed class ReportFile
        {
            public string? Commit { get; set; }
            public string? Ref { get; set; }
            public int Total { get; set; }
            public int Passed { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public long DurationMs { get; set; }
            public List<SuiteFile>? Suites { get; set; }
        }

        private sealed class SuiteFile
        {
            public string? Suite { get; set; }
            public int Total { get; set; }
            public int Passed { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public long DurationMs { get; set; }
            public List<TestFile>? Tests { get; set; }
        }

        private sealed class TestFile
        {
            public string? Name { get; set; }
            public string? Class { get; set; }
            public string? Outcome { get; set; }
            public long DurationMs { get; set; }
            public string? Message { get; set; }
        }
    }
}
