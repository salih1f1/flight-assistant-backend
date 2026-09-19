namespace FlightAssistant.Application.Providers
{
    /// <summary>
    /// The routing decisions an admin can change. A closed list, for the reason <see cref="Jobs.JobNames"/>
    /// gives for its own: this is a small set of deliberate choices, not a feature-flag framework,
    /// and a closed list is what keeps it from becoming one. Adding a key means adding a reader for
    /// it, which is the point.
    /// </summary>
    public static class ProviderJobs
    {
        public const string FlightSearchRoute = "flight-search-route";

        public const string AeroApi = "aeroapi";
        public const string AeroDataBox = "aerodatabox";

        public static readonly IReadOnlyList<string> All = [FlightSearchRoute];

        public static string DisplayNameFor(string job) => job switch
        {
            FlightSearchRoute => "Route search",
            _ => throw Unknown(job)
        };

        /// <summary>
        /// The provider names this key accepts. For verification keys (PR 2) the real answer comes
        /// from <c>IVerificationSourceRegistry</c> instead, since it depends on which sources are
        /// registered and which entity types they support.
        /// </summary>
        public static IReadOnlyList<string> OptionsFor(string job) => job switch
        {
            FlightSearchRoute => [AeroDataBox, AeroApi],
            _ => throw Unknown(job)
        };

        private static InvalidOperationException Unknown(string job) =>
            new($"'{job}' is not a known provider routing key.");
    }
}
