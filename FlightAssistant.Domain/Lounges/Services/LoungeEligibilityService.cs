using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Domain.Lounges.Services
{
    public record EligibilityResult(bool IsEligible, string? Via, LoungeProviderKind? ProviderKind);

    public static class LoungeEligibilityService
    {
        // Alliance tier rank tables — higher rank = higher status
        private static readonly IReadOnlyDictionary<string, int> OwTierRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["RUBY"]     = 1,
            ["SAPPHIRE"] = 2,
            ["EMERALD"]  = 3,
        };

        private static readonly IReadOnlyDictionary<string, int> SaTierRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["SILVER"] = 1,
            ["GOLD"]   = 2,
        };

        private static readonly IReadOnlyDictionary<string, int> StTierRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ELITE"]      = 1,
            ["ELITE_PLUS"] = 2,
        };

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> AllianceTierRanks =
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["OW"] = OwTierRanks,
                ["SA"] = SaTierRanks,
                ["ST"] = StTierRanks,
            };

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
            var today       = DateOnly.FromDateTime(DateTime.UtcNow);
            var activeMemberships = memberships
                .Where(m => m.TierExpiryDate is null || m.TierExpiryDate.Value >= today)
                .ToList();

            foreach (var link in lounge.Providers)
            {
                var provider = link.Provider;

                switch (provider.Kind)
                {
                    case LoungeProviderKind.Airline:
                        var airlineMatch = activeMemberships.Any(m =>
                            programById.TryGetValue(m.ProgramId, out var prog) && prog.Code == provider.Code);
                        if (airlineMatch)
                            return new EligibilityResult(true, provider.Name, LoungeProviderKind.Airline);
                        break;

                    case LoungeProviderKind.Alliance:
                        var allianceMatch = activeMemberships.Any(m =>
                            m.TierId is not null &&
                            tierById.TryGetValue(m.TierId.Value, out var tier) &&
                            tier.AllianceTierCode is not null &&
                            tier.AllianceCode == provider.Code &&
                            MeetsMinTier(provider.Code, tier.AllianceTierCode, link.MinAllianceTierCode));
                        if (allianceMatch)
                            return new EligibilityResult(true, provider.Name, LoungeProviderKind.Alliance);
                        break;

                    case LoungeProviderKind.Contract:
                    case LoungeProviderKind.Card:
                        var contractMatch = activeMemberships.Any(m =>
                            programById.TryGetValue(m.ProgramId, out var prog) && prog.Code == provider.Code);
                        if (contractMatch)
                            return new EligibilityResult(true, provider.Name, provider.Kind);
                        break;
                }
            }

            return new EligibilityResult(false, null, null);
        }

        private static bool MeetsMinTier(string allianceCode, string? memberTierCode, string? minTierCode)
        {
            if (minTierCode is null) return true;
            if (memberTierCode is null) return false;

            if (!AllianceTierRanks.TryGetValue(allianceCode, out var ranks)) return false;

            var memberRank = ranks.GetValueOrDefault(memberTierCode, 0);
            var minRank    = ranks.GetValueOrDefault(minTierCode, 0);
            return memberRank >= minRank;
        }
    }
}
