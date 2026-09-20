using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Providers
{
    /// <summary>Every routing decision and its current value, the full call map -- including the
    /// rows whose provider is fixed and why -- and each provider's remaining headroom. Backs
    /// <c>GET /admin/providers</c>.</summary>
    public sealed record GetProviderRoutesQuery : IRequest<AdminProviderMapDto>;

    public sealed class GetProviderRoutesQueryHandler
        : IRequestHandler<GetProviderRoutesQuery, AdminProviderMapDto>
    {
        private readonly IProviderRouting _routing;
        private readonly IProviderRouteDefaults _defaults;
        private readonly IVerificationSourceRegistry _registry;
        private readonly IProviderBudgetGuard _budget;
        private readonly IAeroApiAccountUsageProvider _usage;
        private readonly ICacheService _cache;
        private readonly IAeroApiBudgetOptions _budgetOptions;
        private readonly IProviderQuotaReader _quota;
        private readonly TimeProvider _timeProvider;

        public GetProviderRoutesQueryHandler(
            IProviderRouting routing,
            IProviderRouteDefaults defaults,
            IVerificationSourceRegistry registry,
            IProviderBudgetGuard budget,
            IAeroApiAccountUsageProvider usage,
            ICacheService cache,
            IAeroApiBudgetOptions budgetOptions,
            IProviderQuotaReader quota,
            TimeProvider timeProvider)
        {
            _routing = routing;
            _defaults = defaults;
            _registry = registry;
            _budget = budget;
            _usage = usage;
            _cache = cache;
            _budgetOptions = budgetOptions;
            _quota = quota;
            _timeProvider = timeProvider;
        }

        public async Task<AdminProviderMapDto> Handle(
            GetProviderRoutesQuery request, CancellationToken ct)
        {
            var routes = new List<AdminProviderRouteDto>(ProviderJobs.All.Count);

            foreach (var job in ProviderJobs.All)
            {
                var state = await _routing.GetAsync(job, ct);
                var ceilinged = _defaults.IsCeilinged(job);

                // Two fields, not one, following AdminJobDto's ConfigEnabled/SwitchEnabled/
                // EffectiveEnabled shape -- keep both, derive the third:
                //   Providers          the stored row (or the configured default when no row exists),
                //                      so IsDefault/ChangedUtc/ChangedBy/Reason all describe the
                //                      same thing as the value beside them;
                //   EffectiveProviders what every real call actually uses right now.
                // They diverge only on the documented rollback path -- a row saying "aerodatabox"
                // exists, then AeroDataBox:ApiKey is pulled -- where every search goes wherever the
                // ceiling forces it while the stored row still says what an operator last chose
                // before the environment changed under them. Reporting only the effective value
                // (this handler's shape before the whole-branch review) published configuration's
                // choice beside a named admin's changedBy/reason with isDefault: false, which reads
                // as that admin having chosen it; reporting only the stored value published a
                // provider no search can reach. The stored row also stays visible in the data
                // browser (BrowsableTables' "provider-routes" entry) for the raw history.
                var effectiveProviders = ceilinged ? _defaults.For(job) : state.Providers;

                // Task 9: a verification key has no static option list (ProviderJobs.OptionsFor
                // returns an empty array for the three of them) -- the real answer is every
                // registered source whose SupportedEntityTypes includes that key's entity type,
                // which only IVerificationSourceRegistry knows. ProviderJobs.SupportedSourceNames
                // is the one place that expression lives, so this can never advertise an option
                // SetProviderRouteCommandHandler's own validation would then reject.
                var options = job == ProviderJobs.FlightSearchRoute
                    ? ProviderJobs.OptionsFor(job)
                    : ProviderJobs.SupportedSourceNames(_registry, ProviderJobs.EntityTypeFor(job));

                routes.Add(new AdminProviderRouteDto(
                    Job: job,
                    DisplayName: ProviderJobs.DisplayNameFor(job),
                    Providers: state.Providers,
                    EffectiveProviders: effectiveProviders,
                    Options: options,
                    IsDefault: state.IsDefault,
                    CanChange: !ceilinged,
                    ChangedUtc: state.ChangedUtc,
                    ChangedBy: state.ChangedBy,
                    Reason: state.Reason));
            }

            // ProviderOperationMap authors each row's providers, which is right for a fixed row and
            // wrong the moment a switchable one is flipped: flip flight-search-route to aeroapi and
            // the authored ["aerodatabox"] would go on claiming AeroDataBox serves route search.
            // That is precisely the failure ProviderOperationMapCompletenessTests names -- "a stale
            // map is worse than no map: it states with apparent authority that a provider is not
            // called" -- so a row carrying a RoutingKey publishes that key's EffectiveProviders from
            // the loop above: one concept, computed once, reused rather than re-derived here.
            // Fixed rows keep exactly what they were authored with. A RoutingKey with no matching
            // route row cannot occur -- EverySwitchableRowNamesAKnownRoutingKey pins every key to
            // ProviderJobs.All, which is the loop's own source -- and falls back to the authored
            // value rather than throwing if it somehow did.
            var effectiveByJob = routes.ToDictionary(
                r => r.Job, r => r.EffectiveProviders, StringComparer.Ordinal);

            var operations = ProviderOperationMap.Entries
                .Select(e => new AdminProviderOperationDto(
                    e.Trigger,
                    e.Operation,
                    e.RoutingKey is not null && effectiveByJob.TryGetValue(e.RoutingKey, out var live)
                        ? live
                        : e.Providers,
                    e.RoutingKey, e.FixedReason, e.ListPrice, e.Note))
                .ToList();

            var headroom = new List<AdminProviderHeadroomDto>
            {
                await BuildAeroApiHeadroomAsync(ct),
                BuildAeroDataBoxHeadroom(),
            };

            return new AdminProviderMapDto(routes, operations, headroom);
        }

        /// <summary>
        /// AeroAPI headroom from the guard and the cached usage report -- the same 10-minute cached
        /// upstream read the usage gauge (<c>GetAeroApiUsageQueryHandler</c>) and the budget guard
        /// already share, resolved through the identical <see cref="UsageRange.Resolve"/> call so the
        /// cache key matches theirs -- so this costs no extra metered call and no extra throttle slot:
        /// whichever of the two reads below reaches AeroAPI first populates the entry the other reads
        /// back. A failed read reports nulls rather than zeros: "cannot tell" is not "nothing spent",
        /// the same rule <see cref="IProviderQuotaReader"/> already follows for AeroDataBox.
        /// </summary>
        /// <remarks>
        /// The catch below discards <c>state</c> too, not just the spend, and that is deliberate, not
        /// merely convenient: <see cref="ProviderBudgetGuard.GetStateAsync"/> never throws -- it has
        /// its own catch-all -- so the only way this method's own fetch can land here is for that same
        /// call, moments earlier in this method, to have already left the shared cache entry cold:
        /// either it skipped reading outright (the month's first 10 minutes, or a cached read-failure
        /// marker), or its own fetch just failed and fell open itself. Either way, <c>state</c> is
        /// never a genuine observation by the time this catch runs -- it is always the guard's
        /// <c>Normal</c> default -- so keeping it would print "Normal" beside a null spend on a
        /// budget-headroom row: a guess dressed up as the one fact an operator most wants, exactly
        /// when it is wrong.
        /// </remarks>
        private async Task<AdminProviderHeadroomDto> BuildAeroApiHeadroomAsync(CancellationToken ct)
        {
            try
            {
                var state = await _budget.GetStateAsync(ct);

                var now = _timeProvider.GetUtcNow();
                var (startUtc, endUtc) = UsageRange.Resolve(
                    new GetAeroApiUsageQuery(), now, AeroApiUsageCache.Duration);
                var (usage, _) = await AeroApiUsageCache.GetOrFetchAsync(
                    _usage, _cache, startUtc, endUtc, ct);

                return new AdminProviderHeadroomDto(
                    Provider: ProviderJobs.AeroApi,
                    BudgetState: state.ToString(),
                    SpentUsd: usage.TotalCostUsd,
                    BudgetUsd: _budgetOptions.MonthlyBudgetUsd,
                    QuotaRemaining: null,
                    QuotaLimit: null,
                    ObservedUtc: null);
            }
            // Verbatim the filter ProviderBudgetGuard uses for this same usage read, rather than the
            // type-only `ex is not OperationCanceledException`: TaskCanceledException derives from
            // OperationCanceledException, so an HttpClient.Timeout on the usage call escapes the
            // type-only form and 502s the whole map -- the one outcome this try/catch exists to
            // prevent, on the page an operator opens *because* a provider is misbehaving. The token,
            // not the exception type, is what tells "our own failure" apart from "the caller
            // abandoned the request". (Unreachable today only because Polly's TotalRequestTimeout
            // fires first and throws TimeoutRejectedException; that is an ordering invariant
            // documented elsewhere, not a property of this method.)
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                return new AdminProviderHeadroomDto(
                    ProviderJobs.AeroApi, null, null, _budgetOptions.MonthlyBudgetUsd, null, null, null);
            }
        }

        /// <summary>
        /// AeroDataBox reports its own quota on its response headers (#531) -- the reader only ever
        /// holds the last reading this process observed, so this makes no call of its own.
        /// </summary>
        private AdminProviderHeadroomDto BuildAeroDataBoxHeadroom()
        {
            var quota = _quota.AeroDataBox;
            return new AdminProviderHeadroomDto(
                Provider: ProviderJobs.AeroDataBox,
                BudgetState: null,
                SpentUsd: null,
                BudgetUsd: null,
                QuotaRemaining: quota?.Remaining,
                QuotaLimit: quota?.Limit,
                ObservedUtc: quota?.ObservedUtc);
        }
    }
}
