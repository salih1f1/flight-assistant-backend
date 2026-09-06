namespace FlightAssistant.Infrastructure.Persistance;

/// <summary>
/// The single place every paged repository computes a SQL <c>OFFSET</c>/<c>LIMIT</c> pair (#316).
/// Ten repositories used to each write <c>.Skip((page - 1) * pageSize).Take(pageSize)</c> inline —
/// plain <see langword="int"/> arithmetic, so a large enough <c>page</c> overflows the
/// multiplication and wraps around to a negative number. Npgsql then rejects the resulting
/// <c>OFFSET must not be negative</c> as an unhandled exception — a 500. The InMemory provider
/// accepts a negative <c>Skip</c> without complaint, which is why this was invisible to the
/// existing (InMemory-backed) test suite; see CLAUDE.md's <c>DateTimeKind</c> entry for the same
/// class of InMemory-vs-Npgsql divergence.
///
/// <para>
/// <see cref="FlightAssistant.Application.Validators.PagedRequestValidator"/> rejects an overflowing
/// <c>(Page, PageSize)</c> pair with a 400 for every caller that goes through the MediatR pipeline.
/// This extension is the backstop underneath it: a repository call that bypasses the validator —
/// today there is none, but nothing stops a future background job or admin tool from calling a
/// repository directly — must still never reach Npgsql with a negative offset, from <i>any</i>
/// combination of arguments, not only an overflowing one. Between the two, the 500 this issue
/// describes is now structurally unreachable regardless of which layer a future caller skips.
/// </para>
///
/// <para>
/// <c>internal</c> deliberately: every call site is a repository in this same assembly, and keeping
/// it out of the public surface means nothing outside Infrastructure can depend on the exact
/// clamping behaviour below. <c>[assembly: InternalsVisibleTo("FlightAssistant.Infrastructure.Tests")]</c>
/// (see <c>Notifications/ApplePushNotificationService.cs</c>) already opens this assembly to its
/// test project — the same seam <c>BrowsableDataRepository.BuildQueryString</c> uses.
/// </para>
/// </summary>
internal static class PagedQueryableExtensions
{
    /// <summary>
    /// Applies <c>Skip(offset).Take(pageSize)</c> with both inputs clamped to the smallest range
    /// that can never reach Npgsql as a negative <c>OFFSET</c>/<c>LIMIT</c> — regardless of which
    /// direction a caller's arguments are wrong in, not only the overflowing-<c>Page</c> direction
    /// #316 was filed against.
    ///
    /// <para>
    /// <b>The offset</b> is computed in <see langword="long"/> — so <c>(page - 1) * pageSize</c>
    /// itself can never overflow — and then <see cref="Math.Clamp(long, long, long)"/>ed to
    /// <c>[0, int.MaxValue]</c>, since <see cref="Queryable.Skip{TSource}"/> only accepts an
    /// <see langword="int"/> and this is the last point before that boundary. Both ends of the
    /// clamp are principled, not arbitrary: <c>int.MaxValue</c> is exactly the largest offset a SQL
    /// query can express, so anything above it is already asking past the end of every table this
    /// process will ever page through, and <c>0</c> is the smallest offset that means anything —
    /// "skip nothing" — so a computed offset that goes negative (a <c>Page</c> below 1, reachable
    /// only by a caller that bypasses <see cref="FlightAssistant.Application.Validators.PagedRequestValidator"/>
    /// entirely) clamps to the same place. <b>Concretely: <c>Page = 0</c> reaching this method
    /// returns the same rows as <c>Page = 1</c>, rather than throwing.</b> That is a deliberate
    /// behaviour choice, not an oversight — every caller that matters is already rejected with a 400
    /// before it gets here, so the one thing this backstop must never do is turn a bypassed validator
    /// into a 500; returning page 1's rows for a nonsensical <c>Page</c> is the same "honest empty
    /// (or nearest) page over an exception" semantics #316 already chose for the overflow direction.
    /// </para>
    ///
    /// <para>
    /// <b>PageSize</b> is clamped to a minimum of <c>0</c> for the same reason: a negative
    /// <c>PageSize</c> reaching <see cref="Queryable.Take{TSource}"/> is <c>LIMIT must not be
    /// negative</c> against real Postgres — a second, independent way to reach the same class of
    /// exception this method exists to close off. Unlike the offset, <c>PageSize</c> has no upper
    /// clamp here: <see cref="FlightAssistant.Application.Validators.PagedRequestValidator"/> caps it
    /// at 200 for every real caller, and there is no analogous "this throws against Postgres" failure
    /// mode on the high side the way there is on the low side — an oversized <c>Take</c> just returns
    /// more rows.
    /// </para>
    /// </summary>
    internal static IQueryable<T> Page<T>(this IQueryable<T> query, int page, int pageSize)
    {
        var offset = ((long)page - 1) * pageSize;
        var clampedOffset = (int)Math.Clamp(offset, 0, int.MaxValue);
        var clampedPageSize = Math.Max(pageSize, 0);
        return query.Skip(clampedOffset).Take(clampedPageSize);
    }
}
