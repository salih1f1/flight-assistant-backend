using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.Application.Commands.Admin.Airlines
{
    /// <summary>
    /// The one check <c>airlines.AllianceCode</c>'s foreign key to <c>alliances.Code</c> needs and no
    /// format rule can express: that the code names an alliance we actually have (#449). Without it a
    /// well-formed but unknown code reaches Postgres and fails
    /// <c>FK_airlines_alliances_AllianceCode</c> as an unhandled <c>DbUpdateException</c> — a 500 that
    /// tells the caller nothing. Same shape, and same reason, as
    /// <c>AcceptVerificationSourceCommand.EnsureCountryExistsAsync</c> does for <c>CountryCode</c>.
    /// <para>
    /// Check-then-save, with the same accepted race that one documents: an alliance deleted between
    /// this lookup and <c>SaveChangesAsync</c> still hits the constraint as a 500.
    /// </para>
    /// </summary>
    public static class AllianceReference
    {
        /// <summary>
        /// Throws a 409 naming the code when <paramref name="allianceCode"/> is not in <c>alliances</c>.
        /// A blank code is no alliance at all — <see cref="CodeNormalization.ToCanonicalCodeOrNull"/>
        /// turns it into the <c>null</c> the column stores, exactly as <c>Airline.Update</c> does — so it
        /// returns without a lookup rather than refusing an airline that simply has no alliance.
        /// Canonicalising first also means the caller's casing never decides the answer.
        /// </summary>
        public static async Task EnsureExistsAsync(
            IAllianceRepository alliances, string? allianceCode, CancellationToken ct)
        {
            var code = allianceCode.ToCanonicalCodeOrNull();
            if (code is null)
                return;

            if (await alliances.FindByCodeAsync(code, ct) is null)
                throw new DomainException(
                    $"Airline AllianceCode {code} is not an alliance we have; add the alliance first.", 409);
        }
    }
}
