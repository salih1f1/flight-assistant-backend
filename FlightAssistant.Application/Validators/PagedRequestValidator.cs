using FlightAssistant.Contracts.Paging;
using FluentValidation;

namespace FlightAssistant.Application.Validators;

public class PagedRequestValidator : AbstractValidator<PagedRequest>
{
    public PagedRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);

        // Every paged repository computes the SQL OFFSET as (page - 1) * pageSize (see
        // PagedQueryableExtensions.Page, the one place that arithmetic now lives). That
        // multiplication is `int` arithmetic at the database layer, and a large enough Page
        // overflows it and wraps around to a negative number — Npgsql then rejects the resulting
        // "OFFSET must not be negative" as an unhandled exception, surfacing as a 500 instead of a
        // 400 that names the problem (#316).
        //
        // The bound below is the overflow boundary itself — (page - 1) * pageSize must still fit in
        // an int — not an arbitrary smaller cap such as "max 10,000 pages". The overflow point is
        // the only limit with a principled justification: it is exactly the condition that breaks,
        // so it rejects nothing that would actually have worked. A smaller round-number cap would
        // reject a legitimately deep page into a large table for no reason this validator could
        // defend.
        //
        // Deliberately reads PageSize off the request rather than closing over a fixed value: the
        // bound is a function of both fields together, not of Page alone. A Page that comfortably
        // fits at PageSize = 1 can overflow at PageSize = 200, so the check has to be redone per
        // request rather than expressed as a single constant ceiling on Page.
        RuleFor(x => x.Page)
            // (long)page - 1, not (long)(page - 1): casting page to long first means the
            // subtraction itself happens in long arithmetic, so this is correct even for a value
            // this rule is never actually reached with in practice (page = int.MinValue would
            // underflow int subtraction before the cast ever got a chance to widen it).
            .Must((request, page) => ((long)page - 1) * request.PageSize <= int.MaxValue)
            .WithMessage(request =>
                $"Page {request.Page} is too large for PageSize {request.PageSize}: the resulting " +
                "SQL offset would overflow a 32-bit integer. Reduce Page or PageSize.");
    }
}
