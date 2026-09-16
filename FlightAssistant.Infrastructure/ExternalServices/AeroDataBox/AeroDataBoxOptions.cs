namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// AeroDataBox via the RapidAPI marketplace (#422). The key is optional — like GitHub:Token,
    /// its absence must not stop the app starting; route search falls back to AeroAPI instead.
    /// </summary>
    public sealed class AeroDataBoxOptions
    {
        public const string RapidApiHost = "aerodatabox.p.rapidapi.com";

        public string? ApiKey { get; set; }
        public string BaseUrl { get; set; } = $"https://{RapidApiHost}/";
        public int TimeoutSeconds { get; set; } = 30;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
