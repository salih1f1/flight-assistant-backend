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
            foreach (var link in lounge.Providers)
            {
                var provider = link.Provider;

                switch (provider.Kind)
                {
                    case LoungeProviderKind.Airline:
                        // Check if traveler has a membership in an airline program matching this provider's airline code
                        var airlineMatch = memberships.Any(m =>
                        {
                            var prog = programs.FirstOrDefault(p => p.Id == m.ProgramId);
                            return prog?.Code == provider.Code;
                        });
                        if (airlineMatch)
                            return new EligibilityResult(true, provider.Name, LoungeProviderKind.Airline);
                        break;

                    case LoungeProviderKind.Alliance:
                        // Check if traveler has a tier matching the provider's alliance code
                        var allianceMatch = memberships.Any(m =>
                        {
                            if (m.TierId is null) return false;
                            var tier = allTiers.FirstOrDefault(t => t.Id == m.TierId);
                            return tier?.AllianceCode == provider.Code;
                        });
                        if (allianceMatch)
                            return new EligibilityResult(true, provider.Name, LoungeProviderKind.Alliance);
                        break;

                    case LoungeProviderKind.Contract:
                    case LoungeProviderKind.Card:
                        // Check if traveler has a membership in a program matching this contract/card provider
                        var contractMatch = memberships.Any(m =>
                        {
                            var prog = programs.FirstOrDefault(p => p.Id == m.ProgramId);
                            return prog?.Code == provider.Code;
                        });
                        if (contractMatch)
                            return new EligibilityResult(true, provider.Name, provider.Kind);
                        break;
                }
            }

            return new EligibilityResult(false, null, null);
        }
    }
}
