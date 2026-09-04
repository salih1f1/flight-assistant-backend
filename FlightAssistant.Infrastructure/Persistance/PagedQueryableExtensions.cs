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
/// repository directly — must still never reach Npgsql with a negative offset. Between the two,
/// the 500 this issue describes is now structurally unreachable regardless of which layer a future
/// caller skips.
/// </para>
/// </summary>
public static class PagedQueryableExtensions
{
    /// <summary>
    /// Applies <c>Skip((page - 1) * pageSize).Take(pageSize)</c> with the offset computed in
    /// <see langword="long"/> — so the multiplication itself can never overflow — and then clamped
    /// to <see cref="int.MaxValue"/>, since <see cref="Queryable.Skip{TSource}"/> only accepts an
    /// <see langword="int"/> and this is the last point before that boundary. The clamp is
    /// deliberately just <see cref="int.MaxValue"/>, not some smaller round number: that is exactly
    /// the largest offset a SQL query can express, so nothing below it would ever need clamping in
    /// the first place, and anything above it is already asking past the end of every table this
    /// process will ever page through. A clamped offset still executes rather than being rejected —
    /// it returns an empty page, the honest answer to "you asked past the end of the table", instead
    /// of the exception plain <see langword="int"/> arithmetic would throw once the multiplication
    /// overflows. <c>PageSize</c> itself needs no clamping here: it is already bounded 1–200 by
    /// <see cref="FlightAssistant.Application.Validators.PagedRequestValidator"/>, so
    /// <c>Take(pageSize)</c> alone is safe.
    /// </summary>
    public static IQueryable<T> Page<T>(this IQueryable<T> query, int page, int pageSize)
    {
        var offset = ((long)page - 1) * pageSize;
        var clampedOffset = offset > int.MaxValue ? int.MaxValue : (int)offset;
        return query.Skip(clampedOffset).Take(pageSize);
    }
}
