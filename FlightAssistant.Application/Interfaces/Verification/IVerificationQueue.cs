using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// The one way to queue automated verification checks (spec §4.1, §4.5). Callers (the admin
    /// endpoint in step 6, the flight-tracking trigger in step 10) depend on this rather than on
    /// the table, so a message signal could be added later without changing them.
    /// </summary>
    public interface IVerificationQueue
    {
        /// <summary>
        /// Queues one <c>Pending</c> <c>VerificationRequest</c> per key and saves immediately.
        ///
        /// <para>
        /// A key is <b>skipped</b>, not queued, when the entity already has an open
        /// (<c>Pending</c>/<c>Running</c>) request. That includes a key repeated within
        /// <paramref name="keys"/>, and a concurrent enqueue that inserts the same entity between
        /// this call's check and its insert: the open-request unique index rejects the insert, and
        /// that rejection counts as skipped rather than failing the call. A completed request never
        /// blocks a new one on its own.
        /// </para>
        ///
        /// <para>
        /// A key for <paramref name="trigger"/> <see cref="VerificationTrigger.Flight"/> is also
        /// skipped, on its own (#418), when the entity's most recently <em>completed</em> request
        /// was <c>Error</c> within <c>Verification:ErrorCooldown</c> — a broken upstream is left
        /// alone for a while rather than re-queued (and re-billed) on every repeat track. This
        /// cooldown never applies to any other trigger. Either reason counts toward
        /// <c>Skipped</c>, so <c>Queued + Skipped == keys.Count</c> still holds.
        /// </para>
        ///
        /// <para>
        /// This saves through the scoped <c>DbContext</c>, so any other pending changes on it are
        /// saved too. Argument validation (blank key, unknown source characters, lengths) is
        /// <c>VerificationRequest.Create</c>'s and throws <see cref="ArgumentException"/> before
        /// anything is written.
        /// </para>
        /// </summary>
        /// <param name="entityType">The type every key belongs to.</param>
        /// <param name="keys">
        /// Entity keys, canonicalised by <c>VerificationRequest.Create</c> before the open-request check,
        /// so one entity is always one key:
        /// <list type="bullet">
        /// <item><c>Country</c>: a two-letter code, upper-cased (<c>gb</c> is stored and matched as <c>GB</c>).</item>
        /// <item><c>Airport</c>, <c>Airline</c>: the positive <see cref="int"/> id in canonical decimal
        /// form, with ASCII digits only, no sign and no leading zeros (<c>42</c>, never <c>042</c> or <c>+42</c>).</item>
        /// </list>
        /// A key that is empty, has surrounding whitespace or is not in that form throws
        /// <see cref="ArgumentException"/>. Keys that canonicalise to the same value count as repeats.
        /// </param>
        /// <param name="sources">Source names to check against, e.g. <c>ourairports</c>.</param>
        /// <param name="trigger">What asked for the checks; <see cref="VerificationTrigger.Flight"/> is
        /// claimed first only among requests younger than <c>Verification:ClaimPromotionAge</c> — an
        /// older request of any trigger is promoted ahead of it (#419). See
        /// <c>IVerificationRequestRepository.ClaimBatchAsync</c>.</param>
        /// <param name="requestedBy">The admin's id, or <c>system</c>.</param>
        /// <param name="estimatedCostUsd">The estimated cost of <b>one</b> request: the sum of the selected
        /// sources' per-row cost (0 for the free sources). Stored on every queued request.</param>
        Task<VerificationEnqueueResult> EnqueueAsync(
            VerificationEntityType entityType,
            IReadOnlyCollection<string> keys,
            IReadOnlyCollection<string> sources,
            VerificationTrigger trigger,
            string requestedBy,
            decimal estimatedCostUsd,
            CancellationToken ct = default);
    }

    /// <summary>What <see cref="IVerificationQueue.EnqueueAsync"/> did: requests created, and keys
    /// skipped — because their entity already had an open request, or, for the flight trigger only,
    /// because its last completed check was a recent <c>Error</c> (#418).</summary>
    public sealed record VerificationEnqueueResult(int Queued, int Skipped);
}
