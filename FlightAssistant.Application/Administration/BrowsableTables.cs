using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Domain.DataSources.Entities;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Jobs.Entities;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Providers.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Domain.Verification.Entities;

namespace FlightAssistant.Application.Administration
{
    /// <summary>
    /// One entry in the <see cref="BrowsableTables"/> registry: a stable URL <paramref name="Slug"/>
    /// for the admin data browser, the CLR entity type it reads, a human <paramref name="DisplayName"/>
    /// for the panel, and the ordered, explicit list of columns the browser is allowed to project and
    /// sort/filter on.
    /// </summary>
    public sealed record BrowsableTable(string Slug, Type EntityType, string DisplayName, IReadOnlyList<string> Columns);

    /// <summary>
    /// The explicit whitelist of tables the admin data browser (#315) may read, and which columns of
    /// each it may expose. Chosen over reflecting on <c>DbContext.Model</c> so that a new entity or
    /// column must be classified <em>deliberately</em> — nothing is ever exposed by accident. That
    /// only holds because <c>BrowsableTablesTests</c> fails the build whenever this registry drifts
    /// from the real EF model, in both directions (a new <c>DbSet</c> unclassified; a new mapped
    /// property on an already-browsable entity left off both the exposed and excluded lists).
    ///
    /// <para>
    /// This registry says what is <em>exposed</em>. It deliberately carries no sensitivity/masking
    /// flags of its own — <see cref="SensitiveProperties"/> is the single source of PII truth, and
    /// A4's projection combines the two: every column below is a candidate for display, and
    /// <see cref="SensitiveProperties.IsSensitive"/> decides which of those get masked. Two
    /// registries that both had to be right would drift exactly the way CLAUDE.md already describes
    /// for validator format rules — here that drift would leak PII instead of just rejecting a valid
    /// request.
    /// </para>
    ///
    /// <para>
    /// Column names are captured with <c>nameof</c> against the real entity types, not as string
    /// literals — a renamed property fails the build here instead of silently vanishing from (or
    /// wrongly surviving in) the browser. <c>BrowsableTablesTests</c> additionally asserts, via
    /// reflection, that every registered <c>(type, column)</c> pair actually resolves.
    /// </para>
    ///
    /// <para>
    /// Slugs are kebab-case and stable — they are URL surface the admin panel hardcodes
    /// (<c>device-tokens</c>, <c>loyalty-program-kinds</c>, <c>admin-audit-log</c>).
    /// </para>
    /// </summary>
    public static class BrowsableTables
    {
        /// <summary>
        /// Entity types deliberately left out of <see cref="Entries"/>, each with a short reason, so
        /// the entity-completeness guard passes for a stated cause rather than an oversight. Default
        /// to including things — this endpoint is read-only, admin-gated and masked — so an entity
        /// belongs here only with a concrete reason not to be browsable.
        /// </summary>
        public static readonly IReadOnlyDictionary<Type, string> NotBrowsable =
            new Dictionary<Type, string>();

        private static readonly IReadOnlyList<BrowsableTable> Entries = new List<BrowsableTable>
        {
            new("travelers", typeof(Traveler), "Travelers", new[]
            {
                nameof(Traveler.Id),
                nameof(Traveler.Email),
                nameof(Traveler.DisplayName),
                nameof(Traveler.CreatedUtc),
                nameof(Traveler.UpdatedUtc),
            }),

            new("device-tokens", typeof(DeviceToken), "Device Tokens", new[]
            {
                nameof(DeviceToken.Id),
                nameof(DeviceToken.TravelerId),
                nameof(DeviceToken.Platform),
                nameof(DeviceToken.Token),
                nameof(DeviceToken.LastSeenUtc),
            }),

            new("flights", typeof(Flight), "Flights", new[]
            {
                nameof(Flight.Id),
                nameof(Flight.AirlineId),
                nameof(Flight.FlightNumber),
                nameof(Flight.DepartureAirportId),
                nameof(Flight.ArrivalAirportId),
                nameof(Flight.ServiceDateUtc),
                nameof(Flight.ScheduledDepartureUtc),
                nameof(Flight.ScheduledArrivalUtc),
                nameof(Flight.EstimatedDepartureUtc),
                nameof(Flight.EstimatedArrivalUtc),
                nameof(Flight.ActualDepartureUtc),
                nameof(Flight.ActualArrivalUtc),
                nameof(Flight.EquipmentAircraftIcao),
                nameof(Flight.CreatedUtc),
                nameof(Flight.UpdatedUtc),
                nameof(Flight.LastSyncedUtc),
                nameof(Flight.NextSyncDueUtc),
                nameof(Flight.SyncMode),
                nameof(Flight.ScheduleMissingSinceUtc),
                nameof(Flight.CancelledAtUtc),
                nameof(Flight.DelayNotifiedDepartureUtc),
            }),

            new("traveler-flights", typeof(TravelerFlight), "Traveler Flights", new[]
            {
                nameof(TravelerFlight.Id),
                nameof(TravelerFlight.TravelerId),
                nameof(TravelerFlight.FlightId),
                nameof(TravelerFlight.Notify),
                nameof(TravelerFlight.CreatedUtc),
            }),

            new("flight-statuses", typeof(FlightStatus), "Flight Statuses", new[]
            {
                nameof(FlightStatus.Id),
                nameof(FlightStatus.Code),
                nameof(FlightStatus.Name),
                nameof(FlightStatus.Description),
            }),

            new("flight-status-events", typeof(FlightStatusEvent), "Flight Status Events", new[]
            {
                nameof(FlightStatusEvent.Id),
                nameof(FlightStatusEvent.FlightId),
                nameof(FlightStatusEvent.StatusId),
                nameof(FlightStatusEvent.EffectiveAtUtc),
                nameof(FlightStatusEvent.RecordedAtUtc),
                nameof(FlightStatusEvent.Source),
                nameof(FlightStatusEvent.ExternalId),
                nameof(FlightStatusEvent.Gate),
                nameof(FlightStatusEvent.Terminal),
                nameof(FlightStatusEvent.BaggageClaim),
                nameof(FlightStatusEvent.Remarks),
                nameof(FlightStatusEvent.RawPayloadJson),
                nameof(FlightStatusEvent.DedupHash),
            }),

            new("lounges", typeof(Lounge), "Lounges", new[]
            {
                nameof(Lounge.Id),
                nameof(Lounge.AirportId),
                nameof(Lounge.Name),
                nameof(Lounge.Terminal),
                nameof(Lounge.LocationHint),
                nameof(Lounge.Hours),
                nameof(Lounge.IsAirside),
                nameof(Lounge.IsDomesticOnly),
                nameof(Lounge.IsInternationalOnly),
                nameof(Lounge.Amenities),
                nameof(Lounge.CreatedUtc),
                nameof(Lounge.IsVerified),
                nameof(Lounge.VerifiedAt),
                nameof(Lounge.VerifiedBy),
                nameof(Lounge.VerificationSource),
                nameof(Lounge.VerificationIssue),
            }),

            new("lounge-providers", typeof(LoungeProvider), "Lounge Providers", new[]
            {
                nameof(LoungeProvider.Id),
                nameof(LoungeProvider.Code),
                nameof(LoungeProvider.Name),
                nameof(LoungeProvider.Kind),
                nameof(LoungeProvider.Description),
                nameof(LoungeProvider.IsVerified),
                nameof(LoungeProvider.VerifiedAt),
                nameof(LoungeProvider.VerifiedBy),
                nameof(LoungeProvider.VerificationSource),
                nameof(LoungeProvider.VerificationIssue),
            }),

            new("lounge-provider-links", typeof(LoungeProviderLink), "Lounge Provider Links", new[]
            {
                nameof(LoungeProviderLink.LoungeId),
                nameof(LoungeProviderLink.ProviderId),
                nameof(LoungeProviderLink.Role),
                nameof(LoungeProviderLink.Notes),
                nameof(LoungeProviderLink.MinAllianceTierCode),
            }),

            new("loyalty-programs", typeof(LoyaltyProgram), "Loyalty Programs", new[]
            {
                nameof(LoyaltyProgram.Id),
                nameof(LoyaltyProgram.Code),
                nameof(LoyaltyProgram.Name),
                nameof(LoyaltyProgram.KindId),
                nameof(LoyaltyProgram.AirlineId),
                nameof(LoyaltyProgram.CreatedUtc),
                nameof(LoyaltyProgram.IsVerified),
                nameof(LoyaltyProgram.VerifiedAt),
                nameof(LoyaltyProgram.VerifiedBy),
                nameof(LoyaltyProgram.VerificationSource),
                nameof(LoyaltyProgram.VerificationIssue),
            }),

            new("loyalty-program-kinds", typeof(LoyaltyProgramKind), "Loyalty Program Kinds", new[]
            {
                nameof(LoyaltyProgramKind.Id),
                nameof(LoyaltyProgramKind.Code),
                nameof(LoyaltyProgramKind.Name),
                nameof(LoyaltyProgramKind.Description),
                nameof(LoyaltyProgramKind.IsVerified),
                nameof(LoyaltyProgramKind.VerifiedAt),
                nameof(LoyaltyProgramKind.VerifiedBy),
                nameof(LoyaltyProgramKind.VerificationSource),
                nameof(LoyaltyProgramKind.VerificationIssue),
            }),

            new("loyalty-tiers", typeof(LoyaltyTier), "Loyalty Tiers", new[]
            {
                nameof(LoyaltyTier.Id),
                nameof(LoyaltyTier.ProgramId),
                nameof(LoyaltyTier.Code),
                nameof(LoyaltyTier.Name),
                nameof(LoyaltyTier.Rank),
                nameof(LoyaltyTier.AllianceCode),
                nameof(LoyaltyTier.AllianceTierCode),
            }),

            new("traveler-loyalty-memberships", typeof(TravelerLoyaltyMembership), "Traveler Loyalty Memberships", new[]
            {
                nameof(TravelerLoyaltyMembership.Id),
                nameof(TravelerLoyaltyMembership.TravelerId),
                nameof(TravelerLoyaltyMembership.ProgramId),
                nameof(TravelerLoyaltyMembership.TierId),
                nameof(TravelerLoyaltyMembership.AccountNumber),
                nameof(TravelerLoyaltyMembership.TierExpiryDate),
                nameof(TravelerLoyaltyMembership.CreatedUtc),
                nameof(TravelerLoyaltyMembership.UpdatedUtc),
            }),

            new("airlines", typeof(Airline), "Airlines", new[]
            {
                nameof(Airline.Id),
                nameof(Airline.Iata),
                nameof(Airline.Icao),
                nameof(Airline.Name),
                nameof(Airline.CountryCode),
                nameof(Airline.AllianceCode),
                nameof(Airline.SourceType),
                nameof(Airline.FirstSeenAt),
                nameof(Airline.IsVerified),
                nameof(Airline.VerifiedAt),
                nameof(Airline.VerifiedBy),
                nameof(Airline.VerificationSource),
                nameof(Airline.VerificationIssue),
            }),

            new("airports", typeof(Airport), "Airports", new[]
            {
                nameof(Airport.Id),
                nameof(Airport.Iata),
                nameof(Airport.Icao),
                nameof(Airport.Name),
                nameof(Airport.City),
                nameof(Airport.CountryCode),
                nameof(Airport.Latitude),
                nameof(Airport.Longitude),
                nameof(Airport.ElevationFt),
                nameof(Airport.SourceType),
                nameof(Airport.FirstSeenAt),
                nameof(Airport.IsVerified),
                nameof(Airport.VerifiedAt),
                nameof(Airport.VerifiedBy),
                nameof(Airport.VerificationSource),
                nameof(Airport.VerificationIssue),
            }),

            new("alliances", typeof(Alliance), "Alliances", new[]
            {
                nameof(Alliance.Id),
                nameof(Alliance.Code),
                nameof(Alliance.Name),
                nameof(Alliance.SourceType),
                nameof(Alliance.FirstSeenAt),
                nameof(Alliance.IsVerified),
                nameof(Alliance.VerifiedAt),
                nameof(Alliance.VerifiedBy),
                nameof(Alliance.VerificationSource),
                nameof(Alliance.VerificationIssue),
            }),

            new("countries", typeof(Country), "Countries", new[]
            {
                nameof(Country.Code),
                nameof(Country.Name),
                nameof(Country.Alpha3),
                nameof(Country.NumericCode),
                nameof(Country.Region),
                nameof(Country.Subregion),
                nameof(Country.IsEU),
                nameof(Country.IsSchengen),
                nameof(Country.IsEEA),
                nameof(Country.SourceType),
                nameof(Country.FirstSeenAt),
                nameof(Country.IsVerified),
                nameof(Country.VerifiedAt),
                nameof(Country.VerifiedBy),
                nameof(Country.VerificationSource),
                nameof(Country.VerificationIssue),
            }),

            new("seed-versions", typeof(SeedVersion), "Seed Versions", new[]
            {
                nameof(SeedVersion.Dataset),
                nameof(SeedVersion.Version),
                nameof(SeedVersion.AppliedAt),
            }),

            new("admin-audit-log", typeof(AdminAuditLog), "Admin Audit Log", new[]
            {
                nameof(AdminAuditLog.Id),
                nameof(AdminAuditLog.ActorId),
                nameof(AdminAuditLog.ActorName),
                nameof(AdminAuditLog.ActionUtc),
                nameof(AdminAuditLog.Action),
                nameof(AdminAuditLog.EntityType),
                nameof(AdminAuditLog.EntityId),
                nameof(AdminAuditLog.Summary),
                nameof(AdminAuditLog.Before),
                nameof(AdminAuditLog.After),
                nameof(AdminAuditLog.TraceId),
            }),

            // Every mapped column except the RowVersion shadow property (PostgreSQL's xmin, the job's
            // concurrency token): it has no CLR property to project, and it means nothing to an admin.
            // BrowsableTablesModelCompletenessTests excludes it explicitly. Nothing here is sensitive:
            // Error holds fixed categories, and RequestedBy is an admin id, like VerifiedBy.
            new("verification-requests", typeof(VerificationRequest), "Verification Requests", new[]
            {
                nameof(VerificationRequest.Id),
                nameof(VerificationRequest.EntityType),
                nameof(VerificationRequest.EntityKey),
                nameof(VerificationRequest.Sources),
                nameof(VerificationRequest.Trigger),
                nameof(VerificationRequest.Status),
                nameof(VerificationRequest.Attempts),
                nameof(VerificationRequest.NextAttemptUtc),
                nameof(VerificationRequest.DifferencesJson),
                nameof(VerificationRequest.SourceValuesJson),
                nameof(VerificationRequest.Error),
                nameof(VerificationRequest.FailedSource),
                nameof(VerificationRequest.EstimatedCostUsd),
                nameof(VerificationRequest.RequestedBy),
                nameof(VerificationRequest.RequestedUtc),
                nameof(VerificationRequest.StartedUtc),
                nameof(VerificationRequest.CompletedUtc),
            }),

            // #460. Written by the health observer's background flush, never by an admin, and the
            // /health page renders a friendlier view of the same nine rows — but browsable all the
            // same, following this registry's "default to including things" rule: the raw row is
            // where consecutive_failures and the verbatim last_error can be read.
            new("data-source-status", typeof(DataSourceStatus), "Data Source Status", new[]
            {
                nameof(DataSourceStatus.Key),
                nameof(DataSourceStatus.LastOutcome),
                nameof(DataSourceStatus.LastOutcomeUtc),
                nameof(DataSourceStatus.LastOkUtc),
                nameof(DataSourceStatus.LastError),
                nameof(DataSourceStatus.ConsecutiveFailures),
                nameof(DataSourceStatus.LastProbeUtc),
                nameof(DataSourceStatus.LastProbeBy),
            }),

            // #471. Toggled through the dedicated admin endpoint (audited explicitly there, see
            // AdminAuditInterceptor's JobSwitch exclusion) — but browsable all the same, following
            // this registry's "default to including things" rule. Nothing here is sensitive:
            // ChangedBy is an admin id, like VerifiedBy elsewhere in this registry.
            new("job-switches", typeof(JobSwitch), "Job Switches", new[]
            {
                nameof(JobSwitch.Job),
                nameof(JobSwitch.Enabled),
                nameof(JobSwitch.ChangedUtc),
                nameof(JobSwitch.ChangedBy),
                nameof(JobSwitch.Reason),
            }),

            // #531. Changed through the dedicated admin endpoint (audited explicitly there, see
            // AdminAuditInterceptor's ProviderRoute exclusion) — browsable all the same, following
            // this registry's "default to including things" rule. Nothing here is sensitive:
            // ChangedBy is an admin id, and Providers is a source name.
            new("provider-routes", typeof(ProviderRoute), "Provider Routes", new[]
            {
                nameof(ProviderRoute.Job),
                nameof(ProviderRoute.Providers),
                nameof(ProviderRoute.ChangedUtc),
                nameof(ProviderRoute.ChangedBy),
                nameof(ProviderRoute.Reason),
            }),
        };

        /// <summary>
        /// Slug lookup, tolerant of duplicate slugs (first entry wins) so a registry bug surfaces as
        /// a clear assertion failure in <c>BrowsableTablesTests</c> rather than a
        /// <see cref="TypeInitializationException"/> that would take out every test in the suite.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, BrowsableTable> BySlug =
            Entries
                .GroupBy(e => e.Slug, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        /// <summary>Every registered entry, in declaration order.</summary>
        public static IReadOnlyList<BrowsableTable> All => Entries;

        /// <summary>The entry for <paramref name="slug"/>, or <c>null</c> if the slug is not registered.</summary>
        public static BrowsableTable? TryGetBySlug(string slug) =>
            BySlug.TryGetValue(slug, out var entry) ? entry : null;

        /// <summary>
        /// Whether <paramref name="columnName"/> is in the exposed-column list for <paramref name="slug"/>.
        /// False for an unregistered slug. Task 2 validates every incoming <c>sort</c>/<c>filter</c>
        /// column name against this before it reaches <c>EF.Property&lt;object&gt;()</c>.
        /// </summary>
        public static bool IsColumnExposed(string slug, string columnName) =>
            TryGetBySlug(slug) is { } table && table.Columns.Contains(columnName, StringComparer.Ordinal);
    }
}
