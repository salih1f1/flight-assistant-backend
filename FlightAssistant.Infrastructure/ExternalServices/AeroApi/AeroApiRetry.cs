namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Marks a single outbound AeroAPI request so the <c>"AeroApi"</c> client's resilience pipeline
    /// doesn't retry it (#375). A scheduled status poll uses it: AeroAPI bills failed calls, and the
    /// sync loop's next due sync is already the retry. Setting and reading the mark both live here,
    /// so the key can't drift between the provider and the pipeline.
    /// </summary>
    public static class AeroApiRetry
    {
        private static readonly HttpRequestOptionsKey<bool> SkipKey = new("AeroApi.SkipRetry");

        public static void Skip(HttpRequestMessage request) => request.Options.Set(SkipKey, true);

        public static bool IsSkipped(HttpRequestMessage? request) =>
            request is not null && request.Options.TryGetValue(SkipKey, out var skip) && skip;
    }
}
