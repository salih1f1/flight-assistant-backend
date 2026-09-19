namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Keys a source may put in <c>VerificationSourceFetchResult.Found</c> that are <b>not</b>
    /// checked columns (#465).
    ///
    /// <para>
    /// <see cref="VerificationComparer"/> reads only the columns
    /// <see cref="VerificationCheckedColumns.GetColumns"/> registers and silently ignores anything
    /// else -- its own documented behaviour -- so a key here can never change a verification
    /// outcome. It exists because reference-data provisioning needs a field the check deliberately
    /// does not compare.
    /// </para>
    ///
    /// <para>
    /// <b>Do not add a key here to avoid registering a real checked column.</b> This is only for
    /// values that genuinely cannot be compared against one of our own columns. Today there is
    /// exactly one: an airline's country, which <c>aeroapi</c> reports as a name ("United Kingdom")
    /// and never as the two-letter code <c>Airline.CountryCode</c> stores -- which is precisely why
    /// #401 R1 removed that column from the airline checked-column list.
    /// </para>
    /// </summary>
    public static class VerificationExtraValues
    {
        /// <summary>The airline's country as the source names it, e.g. <c>"United Kingdom"</c>.
        /// Resolved to a code by <c>ICountryNameResolver</c>.</summary>
        public const string AirlineCountryName = "CountryName";
    }
}
