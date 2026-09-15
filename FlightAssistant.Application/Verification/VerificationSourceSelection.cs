using System.Diagnostics.CodeAnalysis;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// The sources an admin asked to check one entity type against, once each is known to be usable
    /// (#378): shared by enqueue and retry, so both refuse the same sources and price a request the
    /// same way.
    /// </summary>
    /// <param name="SourceNames">The distinct names, in request order.</param>
    /// <param name="CostPerRequestUsd">The sum of each source's <see cref="IVerificationSource.CostPerRowUsd"/>:
    /// the estimated cost of one request, stored on every request queued with these sources.</param>
    public sealed record VerificationSourceSelection(IReadOnlyList<string> SourceNames, decimal CostPerRequestUsd)
    {
        /// <summary>
        /// Resolves <paramref name="names"/> for <paramref name="entityType"/>. Fails when the list
        /// breaks <see cref="VerificationRequest.TryJoinSources"/>'s rule (none, blank, separator
        /// characters, too long joined), when a name is not registered, or when a registered source
        /// does not support the entity type. The queue would throw on the first, and the job would
        /// complete the request as <c>Error</c> on the other two, so none of them is worth queuing.
        /// </summary>
        public static bool TryResolve(
            IVerificationSourceRegistry registry,
            VerificationEntityType entityType,
            IEnumerable<string?> names,
            [NotNullWhen(true)] out VerificationSourceSelection? selection,
            [NotNullWhen(false)] out string? error)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(names);
            selection = null;

            if (!VerificationRequest.TryJoinSources(names, out var joined, out error))
                return false;

            // Split the joined form: TryJoinSources has already dropped repeats, so a source named
            // twice is priced once.
            var distinct = joined.Split(VerificationRequest.SourceSeparator);
            var cost = 0m;
            foreach (var name in distinct)
            {
                var source = registry.TryGet(name);
                if (source is null)
                {
                    error = $"Source '{name}' is not registered.";
                    return false;
                }
                if (!source.SupportedEntityTypes.Contains(entityType))
                {
                    error = $"Source '{name}' does not support {entityType}.";
                    return false;
                }
                cost += source.CostPerRowUsd;
            }

            selection = new VerificationSourceSelection(distinct, cost);
            error = null;
            return true;
        }
    }
}
