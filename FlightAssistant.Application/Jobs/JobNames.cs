namespace FlightAssistant.Application.Jobs
{
    /// <summary>
    /// The jobs an admin can switch off. Deliberately a closed list rather than an open registry:
    /// this is two booleans with a config ceiling, not a feature-flag framework, and a closed list
    /// is what keeps it from growing into one.
    /// </summary>
    public static class JobNames
    {
        public const string FlightSync = "flight-sync";
        public const string Verification = "verification";

        public static readonly IReadOnlyList<string> All = [FlightSync, Verification];
    }
}
