using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Domain.Lounges.Services
{
    public record EligibilityResult(bool IsEligible, string? Via, LoungeProviderKind? ProviderKind);

    public static class LoungeEligibilityService
    {
        /// <summary>
        /// Determines whether a traveler is eligible for a given lounge based on their memberships.
        /// Returns the first matching access route (airline, alliance, contract/card).
        /// </summary>
        public static EligibilityResult CheckEligibility(
            Lounge lounge,
            IReadOnlyList<TravelerLoyaltyMembership> memberships,
            IReadOnlyList<LoyaltyProgram> programs,
            IReadOnlyList<LoyaltyTier> allTiers)
        {
            var programById = programs.ToDictionary(p => p.Id);
            var tierById    = allTiers.ToDictionary(t => t.Id);

            foreach (var link in lounge.Providers)
            {
                var provider = link.Provider;

                switch (provider.Kind)
                {
                    case LoungeProviderKind.Airline:
                        var airlineMatch = memberships.Any(m =>
                            programById.TryGetValue(m.ProgramId, out var prog) && prog.Code == provider.Code);
                        if (airlineMatch)
                            return new EligibilityResult(true, provider.Name, LoungeProviderKind.Airline);
                        break;

                    case LoungeProviderKind.Alliance:
                        var allianceMatch = memberships.Any(m =>
                            m.TierId is not null &&
                            tierById.TryGetValue(m.TierId.Value, out var tier) &&
                            tier.AllianceCode == provider.Code);
                        if (allianceMatch)
                            return new EligibilityResult(true, provider.Name, LoungeProviderKind.Alliance);
                        break;

                    case LoungeProviderKind.Contract:
                    case LoungeProviderKind.Card:
                        var contractMatch = memberships.Any(m =>
                            programById.TryGetValue(m.ProgramId, out var prog) && prog.Code == provider.Code);
                        if (contractMatch)
                            return new EligibilityResult(true, provider.Name, provider.Kind);
                        break;
                }
            }

            return new EligibilityResult(false, null, null);
        }
    }
}
