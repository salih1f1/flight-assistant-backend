using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Ci
{
    /// <summary>Recent runs of the CI workflow, newest first, optionally for one branch.</summary>
    public sealed record GetCiRunsQuery(string? Branch = null, int Limit = GetCiRunsQuery.DefaultLimit)
        : IRequest<IReadOnlyList<CiRun>>
    {
        public const int DefaultLimit = 20;
        public const int MaxLimit = 50;
    }

    public sealed class GetCiRunsQueryValidator : AbstractValidator<GetCiRunsQuery>
    {
        public GetCiRunsQueryValidator()
        {
            RuleFor(x => x.Limit).InclusiveBetween(1, GetCiRunsQuery.MaxLimit);

            // Git can't store a longer branch name; this keeps junk out of the cache key.
            RuleFor(x => x.Branch).MaximumLength(255);
        }
    }

    /// <summary>
    /// Proxies GitHub's run list for the CI workflow, cached for <see cref="CacheDuration"/> so a
    /// panel polling it stays far inside the token's 5,000 requests an hour.
    /// </summary>
    public sealed class GetCiRunsQueryHandler : IRequestHandler<GetCiRunsQuery, IReadOnlyList<CiRun>>
    {
        public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

        private const string CacheKeyPrefix = "ci:runs:";

        private readonly ICiRunProvider _ci;
        private readonly ICacheService _cache;

        public GetCiRunsQueryHandler(ICiRunProvider ci, ICacheService cache)
        {
            _ci = ci;
            _cache = cache;
        }

        public async Task<IReadOnlyList<CiRun>> Handle(GetCiRunsQuery request, CancellationToken ct)
        {
            // A blank branch means every branch, so it shares the no-branch request and cache entry.
            var branch = string.IsNullOrWhiteSpace(request.Branch) ? null : request.Branch;

            // The limit goes first and never contains a colon, so a branch that does can't collide.
            var key = $"{CacheKeyPrefix}{request.Limit}:{branch}";

            var runs = await _cache.GetAsync<IReadOnlyList<CiRun>>(key, ct);
            if (runs is null)
            {
                runs = await _ci.GetRunsAsync(branch, request.Limit, ct);
                await _cache.SetAsync(key, runs, CacheDuration, ct);
            }

            return runs;
        }
    }
}
