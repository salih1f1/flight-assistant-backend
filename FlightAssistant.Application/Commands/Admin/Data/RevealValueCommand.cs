using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Data
{
    /// <summary>
    /// The full, unmasked value of one sensitive column on one row — the audited escape hatch
    /// around the data browser's server-side masking (#315/#313). Backs
    /// <c>POST /admin/data/{table}/{id}/reveal</c>.
    /// </summary>
    /// <param name="Table">A <c>BrowsableTables</c> slug — see <c>GetBrowsableDataQuery.Table</c>'s
    /// doc comment for why an unknown slug is resolved to a 404 by the endpoint before this is ever
    /// sent, and re-checked defensively here for any other caller.</param>
    /// <param name="Id">
    /// The row's primary key, stringified exactly as <see cref="IBrowsableDataRepository.GetRowAsync"/>
    /// expects — a single key's value as-is, or a composite key's parts joined with <c>|</c>.
    /// </param>
    /// <param name="Column">
    /// The column to reveal. Must be both exposed by <c>BrowsableTables</c> and registered in
    /// <c>SensitiveProperties</c> for the table's entity type — enforced by
    /// <see cref="RevealValueCommandValidator"/>, so <see cref="RevealValueCommandHandler"/> never
    /// runs at all for a column that fails either check. A perfectly ordinary, already-unmasked
    /// column is rejected exactly the same as an unknown one: this endpoint is a reveal, not a
    /// second way to read a row.
    /// </param>
    public sealed record RevealValueCommand(string Table, string Id, string Column) : IRequest<RevealResult>;

    /// <summary>The revealed value, plus enough of the request back to identify what it belongs
    /// to. Nothing else — see #318's response-shape rule.</summary>
    public sealed record RevealResult(string Table, string Id, string Column, object? Value);

    public sealed class RevealValueCommandValidator : AbstractValidator<RevealValueCommand>
    {
        public RevealValueCommandValidator()
        {
            RuleFor(x => x.Id).NotEmpty();
            RuleFor(x => x.Column).NotEmpty();

            RuleFor(x => x)
                .Must(BeARevealableSensitiveColumn)
                .WithName("column")
                .WithMessage(ColumnErrorMessage);
        }

        // Deliberately treats an unresolved table slug as "valid" here too, exactly like
        // GetBrowsableDataQueryValidator's identical rules: table existence is a 404, decided by
        // the endpoint before this validator ever runs (AdminDataEndpoints), and this validator
        // only ever produces 400s. Re-flagging an already-404'd table as a column validation
        // failure here would just race the two status codes against each other for no benefit.
        private static bool BeARevealableSensitiveColumn(RevealValueCommand command)
        {
            if (string.IsNullOrEmpty(command.Column)) return true; // the NotEmpty rule above already covers this
            var table = BrowsableTables.TryGetBySlug(command.Table);
            if (table is null) return true;

            // The inverse of GetBrowsableDataQueryValidator's sort/filter rule: those reject a
            // sensitive column, this rejects everything that ISN'T one. A column absent from
            // BrowsableTables.Columns entirely and a column that's exposed-but-not-sensitive both
            // fail this the same way — a reveal of an ordinary column is exactly as wrong as a
            // reveal of a made-up one, and both are a 400, not a lookup.
            return BrowsableTables.IsColumnExposed(command.Table, command.Column)
                && SensitiveProperties.IsSensitive(table.EntityType, command.Column);
        }

        private static string ColumnErrorMessage(RevealValueCommand command)
        {
            var table = BrowsableTables.TryGetBySlug(command.Table);
            if (table is null || string.IsNullOrEmpty(command.Column))
                return "Invalid column.";

            return $"Column '{command.Column}' is not a revealable sensitive column on '{command.Table}'.";
        }
    }

    public sealed class RevealValueCommandHandler : IRequestHandler<RevealValueCommand, RevealResult>
    {
        private readonly IBrowsableDataRepository _dataRepository;
        private readonly IAdminAuditRepository _auditRepository;
        private readonly IAdminContext _adminContext;

        public RevealValueCommandHandler(
            IBrowsableDataRepository dataRepository,
            IAdminAuditRepository auditRepository,
            IAdminContext adminContext)
        {
            _dataRepository = dataRepository;
            _auditRepository = auditRepository;
            _adminContext = adminContext;
        }

        public async Task<RevealResult> Handle(RevealValueCommand request, CancellationToken ct)
        {
            // Re-resolved defensively even though the endpoint already checked (AdminDataEndpoints)
            // and the validator's own column rule treats an unknown slug as "valid" for exactly
            // that reason — mirrors GetBrowsableDataQueryHandler's identical re-check.
            var table = BrowsableTables.TryGetBySlug(request.Table)
                ?? throw new DomainException($"Unknown table '{request.Table}'.", 404);

            // The id lookup happens before anything about an audit row is built. Nothing was
            // revealed if no such row exists, so nothing is recorded — an audit row for a 404
            // would itself be a false statement ("actor revealed X"), and it's the one ordering
            // mistake #318 explicitly calls out as easy to get wrong.
            var row = await _dataRepository.GetRowAsync(table, request.Id, ct)
                ?? throw new DomainException($"No '{table.DisplayName}' row with id '{request.Id}'.", 404);

            var value = row[request.Column];

            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId))
            {
                // Unreachable in production: this handler only ever runs behind the AdminOnly
                // policy, and the admin endpoint-group filter (Program.cs) populates ActorId from
                // the oid/sub claim for every request that gets this far. Guarded anyway rather
                // than trusting that invariant silently — AdminAuditLog.Create would otherwise
                // throw its own DomainException("ActorId is required.", 400), which would
                // misreport a pipeline bug as a caller error.
                throw new InvalidOperationException(
                    "RevealValueCommand requires an authenticated admin actor; IAdminContext.ActorId was not set.");
            }

            // ==================================================================================
            // The ordering is the feature (#318). Everything above this line is an ordinary,
            // side-effect-free read. From here on: construct the audit row, commit it, and only
            // THEN hand the value back — never the other way around. If SaveChangesAsync below
            // throws, that exception propagates straight out of Handle (nothing here catches it),
            // so `return` on the last line is never reached: the caller gets a 500
            // (GlobalExceptionHandler's unhandled-exception path) and no value, never a value with
            // no audit row behind it. See
            // RevealValueCommandHandlerTests.Handle_WhenTheAuditSaveFails_PropagatesAndNeverReturnsAValue,
            // which fails if this were ever reordered — the comment alone proves nothing.
            //
            // Before/After are left at their default (null): writing `value` into either — or into
            // Summary — would put the plaintext straight back into admin_audit_log, undoing #313's
            // masking-on-write and turning this audit table into exactly the exfiltration path the
            // reveal audit exists to detect. Summary names the column, never the value.
            var auditLog = AdminAuditLog.Create(
                actorId: actorId,
                actorName: _adminContext.ActorName,
                action: AdminAuditAction.Reveal,
                entityType: table.EntityType.Name,
                entityId: request.Id,
                summary: $"Revealed {request.Column} on {table.EntityType.Name} {request.Id}");

            await _auditRepository.AddAsync(auditLog, ct);
            await _auditRepository.SaveChangesAsync(ct);
            // ==================================================================================

            return new RevealResult(request.Table, request.Id, request.Column, value);
        }
    }
}
