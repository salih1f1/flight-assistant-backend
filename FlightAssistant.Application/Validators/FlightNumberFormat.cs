namespace FlightAssistant.Application.Validators
{
    /// <summary>
    /// The single definition of what an IATA flight designator looks like, shared by every
    /// validator that accepts one.
    ///
    /// <para>
    /// It lives here because it drifted once and that drift was the bug: search's rule accepted an
    /// all-digit prefix ("12345") and rejected a trailing letter, while track's rule did the
    /// opposite — so a flight number the picker happily returned could be refused by
    /// <c>POST /flights</c>, which is precisely the class of defect the search-contract reshape
    /// exists to remove. Two copies of a regex cannot be kept in step by good intentions; one
    /// constant can.
    /// </para>
    /// </summary>
    public static class FlightNumberFormat
    {
        /// <summary>
        /// A real IATA flight designator's 2-character airline prefix always contains at least one
        /// letter (e.g. "6E", "U2", "9W", "B6") — an all-digit prefix like "12" is not a carrier.
        /// <c>[A-Z][A-Z0-9]</c> covers letter-first prefixes (letter+letter and letter+digit);
        /// <c>[0-9][A-Z]</c> covers digit-first ones. Then 1–4 digits and an optional trailing
        /// letter (an operational suffix, e.g. "BA123A"). Whether a given shape is an
        /// actually-assigned carrier is reference data's job — see <c>TrackFlightCommandHandler</c>'s
        /// 404 — not this regex's. Always applied with <c>RegexOptions.IgnoreCase</c>: input case is
        /// normalised, not rejected.
        /// </summary>
        public const string Pattern = @"^(?:[A-Z][A-Z0-9]|[0-9][A-Z])\d{1,4}[A-Z]?$";

        /// <summary>Human-readable rendering of <see cref="Pattern"/>, for validation messages and
        /// API documentation, so those cannot drift from the rule either.</summary>
        public const string Description =
            "a 2-character airline prefix containing at least one letter, followed by 1–4 digits " +
            "and an optional trailing letter (e.g. BA123, 6E4120, BA123A)";
    }
}
