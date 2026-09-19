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

                // #531 fix round 1, Finding 2: when configuration ceilings this key, report the
                // effective providers (the configured default), not the stored row's. On the
                // documented rollback path -- a row saying e.g. "aerodatabox" exists, then
                // AeroDataBox:ApiKey is pulled -- the stored row and reality diverge: every real
                // search now goes wherever the ceiling forces it, while the stored row still says
                // what an operator last chose before the environment changed under it. Reporting
                // the stored value here would have this endpoint say "aerodatabox, cannot change"
                // while every search actually used AeroAPI -- the same drift AdminJobDto.EffectiveEnabled
                // exists to prevent for job switches. The stored row itself is unaffected by this
                // and remains visible in the data browser (BrowsableTables' "provider-routes"
                // entry) for an operator who needs the raw history.
                var providers = ceilinged ? _defaults.For(job) : state.Providers;

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
                    Providers: providers,
                    Options: options,
                    IsDefault: state.IsDefault,
                    CanChange: !ceilinged,
                    FixedReason: null,
                    ChangedUtc: state.ChangedUtc,
                    ChangedBy: state.ChangedBy,
                    Reason: state.Reason));
            }

            var operations = ProviderOperationMap.Entries
                .Select(e => new AdminProviderOperationDto(
                    e.Trigger, e.Operation, e.Providers, e.RoutingKey, e.FixedReason, e.ListPrice, e.Note))
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
