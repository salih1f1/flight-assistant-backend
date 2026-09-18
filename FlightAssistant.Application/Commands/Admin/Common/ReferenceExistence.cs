using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.Application.Commands.Admin.Common
{
    /// <summary>
    /// The check every admin reference-data write needs and no format rule can express: that a
    /// foreign key names a row we actually have (#449, #455). The validators check shape only —
    /// <c>^[A-Z]{2}$</c>, <c>GreaterThan(0)</c> — so without this a well-formed but unknown value
    /// reached Postgres and failed its constraint as an unhandled <c>DbUpdateException</c>: a 500
    /// naming nothing the caller could act on. Every method throws
    /// <see cref="DomainException"/> with a 409 instead, naming the column and the value.
    /// <para>
    /// All of them live here rather than beside each command so the rule has one home. Two
    /// registries of the same idea is the drift CLAUDE.md warns about for validator format rules —
    /// here it would mean one entity silently keeping the 500 the others no longer produce.
    /// </para>
    /// <para>
    /// Every method is check-then-save, with the accepted race
    /// <c>AcceptVerificationSourceCommand.EnsureUniqueValuesAreFreeAsync</c> already documents: a
    /// row deleted between the lookup and <c>SaveChangesAsync</c> still hits the constraint as a
    /// 500. Narrowing that would mean a transaction per write, which these commands do not have.
    /// </para>
    /// <para>
    /// An <b>optional</b> reference left unset is not an unknown one: a blank code or a null id
    /// returns without a lookup, so an airline with no alliance and a programme with no airline
    /// cost nothing and are never refused.
    /// </para>
    /// </summary>
    public static class ReferenceExistence
    {
        /// <summary>
        /// Refuses an <c>AllianceCode</c> that is not in <c>alliances</c>, which
        /// <c>airlines.AllianceCode</c> references by foreign key (#449). Canonicalises first, so a
        /// blank code is "no alliance" — exactly what <c>Airline.Update</c> stores — and the
        /// caller's casing never decides the answer.
        /// </summary>
        public static async Task EnsureAllianceExistsAsync(
            IAllianceRepository alliances, string? allianceCode, CancellationToken ct)
        {
            var code = allianceCode.ToCanonicalCodeOrNull();
            if (code is null)
                return;

            if (await alliances.FindByCodeAsync(code, ct) is null)
                throw Missing("Airline", nameof(Domain.ReferenceData.Entities.Airline.AllianceCode), code,
                    "an alliance", "the alliance");
        }

        /// <summary>
        /// Refuses a <c>CountryCode</c> that is not in <c>countries</c>, which both
        /// <c>airports</c> and <c>airlines</c> reference by foreign key. Same rule, and the same
        /// message, as <c>AcceptVerificationSourceCommand.EnsureCountryExistsAsync</c> applies on
        /// the accept path — a source can know a code we do not (for example <c>XK</c>).
        /// </summary>
        public static async Task EnsureCountryExistsAsync(
            ICountryRepository countries, string? countryCode, string entity, CancellationToken ct)
        {
            var code = countryCode.ToCanonicalCodeOrNull();
            if (code is null)
                return;

            if (await countries.FindByCodeAsync(code, ct) is null)
                throw Missing(entity, "CountryCode", code, "a country", "the country");
        }

        /// <summary>
        /// Refuses an <c>AirportId</c> that is not in <c>airports</c>, which <c>lounges</c>
        /// references by foreign key.
        /// </summary>
        public static async Task EnsureAirportExistsAsync(
            IAirportRepository airports, int airportId, string entity, CancellationToken ct)
        {
            if (await airports.GetByIdAsync(airportId, ct) is null)
                throw Missing(entity, "AirportId", airportId.ToString(), "an airport", "the airport");
        }

        /// <summary>
        /// Refuses an <c>AirlineId</c> that is not in <c>airlines</c>, which
        /// <c>loyalty_programs</c> references by foreign key. The id is optional there, so a null
        /// one is "no airline" and costs no lookup.
        /// </summary>
        public static async Task EnsureAirlineExistsAsync(
            IAirlineRepository airlines, int? airlineId, string entity, CancellationToken ct)
        {
            if (airlineId is not { } id)
                return;

            if (await airlines.GetByIdAsync(id, ct) is null)
                throw Missing(entity, "AirlineId", id.ToString(), "an airline", "the airline");
        }

        /// <summary>
        /// Refuses a <c>KindId</c> that is not in <c>loyalty_program_kinds</c>, which
        /// <c>loyalty_programs</c> references by foreign key.
        /// </summary>
        public static async Task EnsureLoyaltyProgramKindExistsAsync(
            ILoyaltyRepository loyalty, int kindId, CancellationToken ct)
        {
            if (await loyalty.FindKindByIdAsync(kindId, ct) is null)
                throw Missing("LoyaltyProgram", "KindId", kindId.ToString(),
                    "a loyalty program kind", "the kind");
        }

        /// <summary>
        /// Refuses a <c>ProgramId</c> that is not in <c>loyalty_programs</c>, and a <c>TierId</c>
        /// that is not one of <i>that</i> program's tiers — both foreign keys of
        /// <c>traveler_loyalty_memberships</c>, and the pair is only meaningful together: a tier id
        /// borrowed from another program passes every foreign key and still describes a membership
        /// nobody holds (#475). The tier is optional, so a null one costs no check.
        /// </summary>
        public static async Task EnsureLoyaltyProgramAndTierExistAsync(
            ILoyaltyRepository loyalty, int programId, int? tierId, CancellationToken ct)
        {
            // Includes the tiers, so the pair is checked against one read.
            var program = await loyalty.GetProgramByIdAsync(programId, ct)
                ?? throw Missing("TravelerLoyaltyMembership", "ProgramId", programId.ToString(),
                    "a loyalty program", "the program");

            if (tierId is not { } id || program.Tiers.Any(t => t.Id == id))
                return;

            throw new DomainException(
                $"TravelerLoyaltyMembership TierId {id} is not a tier of loyalty program {programId}; " +
                "choose one of that program's tiers.", 409);
        }

        private static DomainException Missing(
            string entity, string column, string value, string article, string subject) =>
            new($"{entity} {column} {value} is not {article} we have; add {subject} first.", 409);
    }
}
