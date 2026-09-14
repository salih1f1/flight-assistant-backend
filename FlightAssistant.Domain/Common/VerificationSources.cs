namespace FlightAssistant.Domain.Common
{
    /// <summary>
    /// The single registry of <see cref="Interfaces.IVerifiable.VerificationSource"/> values, so a
    /// source name is typed once rather than as a string literal at every call site. #355 Task 1
    /// added <see cref="Manual"/>; #362 adds <see cref="Import"/> — the auto-verification sources
    /// (<c>ourairports</c>, <c>aeroapi</c>, <c>restcountries</c>, and combinations of them joined
    /// with <c>+</c>) arrive with the later steps that actually call those providers.
    /// </summary>
    public static class VerificationSources
    {
        /// <summary>An admin verified (or unverified) the row by hand via the admin panel.</summary>
        public const string Manual = "manual";

        /// <summary>
        /// A CSV import stamped the row because the file carried no verification columns for it
        /// (an older export, or none of the three filled in). A row whose CSV carried all three
        /// values instead keeps them as-is — this source is not applied in that case.
        /// </summary>
        public const string Import = "import";
    }
}
