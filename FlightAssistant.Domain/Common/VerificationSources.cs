namespace FlightAssistant.Domain.Common
{
    /// <summary>
    /// The single registry of <see cref="Interfaces.IVerifiable.VerificationSource"/> values, so a
    /// source name is typed once rather than as a string literal at every call site. This step
    /// (#355 Task 1) adds only <see cref="Manual"/> — the auto-verification sources
    /// (<c>ourairports</c>, <c>aeroapi</c>, <c>restcountries</c>, and combinations of them joined
    /// with <c>+</c>) arrive with the later steps that actually call those providers.
    /// </summary>
    public static class VerificationSources
    {
        /// <summary>An admin verified (or unverified) the row by hand via the admin panel.</summary>
        public const string Manual = "manual";
    }
}
