namespace FlightAssistant.Application.Providers
{
    /// <param name="RoutingKey">The <see cref="ProviderJobs"/> key that decides this row, or null
    /// when the row is fixed.</param>
    /// <param name="FixedReason">Why this row cannot be changed, or null when it can. A fixed row is
    /// on the map precisely so the reason is visible rather than buried in a comment.</param>
    /// <param name="ListPrice">Authored, with its own date. Neither provider exposes a price list
    /// through an API, so this cannot be derived; stamping the date makes staleness visible instead
    /// of silent, the same posture as a null <c>dataAsOfUtc</c> on Azure cost.</param>
    /// <param name="Note">A consequence a reader needs before acting on this row, distinct from
    /// <paramref name="FixedReason"/>: a switchable row can still carry one. Optional.</param>
    public sealed record ProviderOperation(
        string Trigger,
        string Operation,
        IReadOnlyList<string> Providers,
        string? RoutingKey,
        string? FixedReason,
        string ListPrice,
        string? Note = null);

    /// <summary>
    /// Which provider is called on which operation. Authored rather than scanned, following
    /// <c>DataSourceCatalog</c>'s own precedent: the trigger prose and the fixed-reasons cannot be
    /// derived from the code, and <c>ProviderOperationMapCompletenessTests</c> is what keeps an
    /// authored list honest.
    /// </summary>
    public static class ProviderOperationMap
    {
        public const string AeroApi = ProviderJobs.AeroApi;
        public const string AeroDataBox = ProviderJobs.AeroDataBox;
        public const string OurAirports = "ourairports";
        public const string Mledoze = "mledoze";

        public static readonly IReadOnlyList<ProviderOperation> Entries =
        [
            new("Tracking a flight", "POST /flights",
                [], null,
                "Tracking itself calls no provider. A verification source is called only when the " +
                "airline, an airport or a country is missing and has to be provisioned (#465), which " +
                "the three verification rows below decide.",
                "$0.00"),

            new("Searching", "GET /flights/search — both airports, no flight number",
                [AeroDataBox], ProviderJobs.FlightSearchRoute, null,
                "AeroDataBox $0.00 (free tier, 2 calls per search) · AeroAPI $0.020/page, list as of 2026-09-19",
                Note:
                "Routing this to AeroAPI puts it back behind AeroApiFlightSearchBudgetDecorator, " +
                "which refuses from SoftLimit (80% of AeroApi:MonthlyBudgetUsd) onward — so this flip " +
                "can turn route searches into 503s at exactly the moment a cost-conscious operator " +
                "reaches for it. Cached searches are still answered."),

            new("Searching", "GET /flights/search — anchored on a flight number",
                [AeroApi], null,
                "AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028), so " +
                "routing this there could change which flight a traveler tracks (#422). A correctness " +
                "rule, not a cost preference.",
                "AeroAPI $0.020/page, list as of 2026-09-19"),

            new("Searching", "GET /airports/{id} — origin timezone, AeroAPI searches only",
                [AeroApi], null,
                "Follows whichever provider serves the search. AeroDataBox needs no timezone lookup: " +
                "its rows carry local times with the UTC offset embedded. Cached 365 days.",
                "AeroAPI $0.015/call, list as of 2026-09-19"),

            new("Status sync", "GET /flights/{ident} — Fast and Live modes",
                [AeroApi], null,
                "The only implementation. AeroDataBox's flight-number endpoint carries no gate and no " +
                "baggage claim, so adopting it would lose fields FlightStatusEvent.CreateDetailChange " +
                "records (#351).",
                "AeroAPI $0.005/page, list as of 2026-09-19"),

            new("Status sync", "GET /schedules — Schedule mode's daily check (#444)",
                [AeroApi], null,
                "Runs as a flight-number search, so it follows that row and cannot be routed to " +
                "AeroDataBox's free tier for the same reason.",
                "AeroAPI $0.020/page, list as of 2026-09-19"),

            new("Verification", "Airport checks",
                [OurAirports], ProviderJobs.VerificationAirport, null,
                "ourairports $0.00 · aeroapi $0.015/lookup, list as of 2026-09-19"),

            new("Verification", "Airline checks",
                [AeroApi], ProviderJobs.VerificationAirline, null,
                "aeroapi $0.015/lookup, list as of 2026-09-19 — the priciest verification check here, with no cheaper source registered for Airline"),

            new("Verification", "Country checks",
                [Mledoze], ProviderJobs.VerificationCountry, null,
                "mledoze $0.00, list as of 2026-09-19"),

            new("Budget and usage", "GET /account/usage",
                [AeroApi], null,
                "The figure the usage gauge and the budget guard both read. FlightAware bills this " +
                "endpoint at $0.00, and one cached read per 10 minutes serves both.",
                "AeroAPI $0.00"),
        ];
    }
}
