using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Lounges.Services;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Domain.Tests.Lounges
{
    public class LoungeEligibilityTests
    {
        // --- Helpers ---

        private static Lounge BuildLounge(LoungeProviderKind kind, string providerCode,
            string? minAllianceTierCode = null)
        {
            var provider = LoungeProvider.Create(providerCode, providerCode + " Lounge", kind);
            var link = LoungeProviderLink.Create(1, 1, LoungeProviderRole.Operator,
                minAllianceTierCode: minAllianceTierCode);

            // Provider has private setter — set via reflection (EF Core does the same at runtime)
            typeof(LoungeProviderLink)
                .GetProperty(nameof(LoungeProviderLink.Provider))!
                .SetValue(link, provider);

            var lounge = Lounge.Create(1, "Test Lounge");
            lounge.ReplaceAllProviders(new[] { link });
            return lounge;
        }

        private static TravelerLoyaltyMembership Membership(
            int programId, string accountNumber = "ACC1",
            int? tierId = null,
            DateOnly? tierExpiryDate = null)
            => TravelerLoyaltyMembership.Create(Guid.NewGuid(), programId, accountNumber, tierId, tierExpiryDate);

        private static LoyaltyProgram Program(int id, string code)
        {
            var p = LoyaltyProgram.Create(code, code + " Program", kindId: 1);
            typeof(LoyaltyProgram).GetProperty(nameof(LoyaltyProgram.Id))!.SetValue(p, id);
            return p;
        }

        private static LoyaltyTier Tier(int id, string allianceCode, string allianceTierCode)
        {
            var t = LoyaltyTier.Create(1, allianceTierCode, allianceTierCode, 1,
                allianceCode: allianceCode, allianceTierCode: allianceTierCode);
            typeof(LoyaltyTier).GetProperty(nameof(LoyaltyTier.Id))!.SetValue(t, id);
            return t;
        }

        // --- Airline eligibility ---

        [Fact]
        public void CheckEligibility_AirlineMatch_ReturnsEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Airline, "BA");
            var program = Program(1, "BA");
            var membership = Membership(programId: 1);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [program], []);

            Assert.True(result.IsEligible);
            Assert.Equal(LoungeProviderKind.Airline, result.ProviderKind);
        }

        [Fact]
        public void CheckEligibility_NoMatchingAirlineProgram_ReturnsNotEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Airline, "BA");
            var program = Program(1, "LH");
            var membership = Membership(programId: 1);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [program], []);

            Assert.False(result.IsEligible);
        }

        // --- Alliance eligibility ---

        [Fact]
        public void CheckEligibility_AllianceTierMeetsMinimum_ReturnsEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Alliance, "OW", minAllianceTierCode: "SAPPHIRE");
            var tier = Tier(10, "OW", "EMERALD"); // rank 3 >= required rank 2
            var membership = Membership(programId: 1, tierId: 10);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [], [tier]);

            Assert.True(result.IsEligible);
            Assert.Equal(LoungeProviderKind.Alliance, result.ProviderKind);
        }

        [Fact]
        public void CheckEligibility_AllianceTierBelowMinimum_ReturnsNotEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Alliance, "OW", minAllianceTierCode: "EMERALD");
            var tier = Tier(10, "OW", "RUBY"); // rank 1 < required rank 3
            var membership = Membership(programId: 1, tierId: 10);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [], [tier]);

            Assert.False(result.IsEligible);
        }

        [Fact]
        public void CheckEligibility_AllianceTierCodeIsNull_ReturnsNotEligible()
        {
            // Tier exists but has no AllianceTierCode — not a proper alliance tier
            var lounge = BuildLounge(LoungeProviderKind.Alliance, "OW");
            var tier = LoyaltyTier.Create(1, "BASIC", "Basic", 1, allianceCode: "OW", allianceTierCode: null);
            typeof(LoyaltyTier).GetProperty(nameof(LoyaltyTier.Id))!.SetValue(tier, 10);
            var membership = Membership(programId: 1, tierId: 10);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [], [tier]);

            Assert.False(result.IsEligible);
        }

        [Fact]
        public void CheckEligibility_UnknownAllianceCode_ReturnsNotEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Alliance, "XX", minAllianceTierCode: "GOLD");
            var tier = Tier(10, "XX", "GOLD");
            var membership = Membership(programId: 1, tierId: 10);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [], [tier]);

            Assert.False(result.IsEligible);
        }

        [Theory]
        [InlineData("SA", "SILVER", "SILVER", true)]
        [InlineData("SA", "SILVER", "GOLD", false)]
        [InlineData("SA", "GOLD", "SILVER", true)]
        [InlineData("ST", "ELITE", "ELITE", true)]
        [InlineData("ST", "ELITE", "ELITE_PLUS", false)]
        [InlineData("ST", "ELITE_PLUS", "ELITE", true)]
        public void CheckEligibility_AllianceTierComparisons(string allianceCode, string memberTierCode, string minTierCode, bool expectedEligible)
        {
            var lounge = BuildLounge(LoungeProviderKind.Alliance, allianceCode, minAllianceTierCode: minTierCode);
            var tier = Tier(10, allianceCode, memberTierCode);
            var membership = Membership(programId: 1, tierId: 10);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [], [tier]);

            Assert.Equal(expectedEligible, result.IsEligible);
        }

        // --- Contract / Card eligibility ---

        [Fact]
        public void CheckEligibility_ContractProgramMatch_ReturnsEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Contract, "DRAGONPASS");
            var program = Program(1, "DRAGONPASS");
            var membership = Membership(programId: 1);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [program], []);

            Assert.True(result.IsEligible);
            Assert.Equal(LoungeProviderKind.Contract, result.ProviderKind);
        }

        // --- Tier expiry ---

        [Fact]
        public void CheckEligibility_ExpiredTier_ReturnsNotEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Airline, "BA");
            var program = Program(1, "BA");
            var expiredMembership = Membership(programId: 1,
                tierExpiryDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [expiredMembership], [program], []);

            Assert.False(result.IsEligible);
        }

        [Fact]
        public void CheckEligibility_TierExpiringToday_ReturnsEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Airline, "BA");
            var program = Program(1, "BA");
            var expiringToday = Membership(programId: 1,
                tierExpiryDate: DateOnly.FromDateTime(DateTime.UtcNow));

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [expiringToday], [program], []);

            Assert.True(result.IsEligible);
        }

        [Fact]
        public void CheckEligibility_NoExpiryDate_ReturnsEligible()
        {
            var lounge = BuildLounge(LoungeProviderKind.Airline, "BA");
            var program = Program(1, "BA");
            var membership = Membership(programId: 1, tierExpiryDate: null);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [program], []);

            Assert.True(result.IsEligible);
        }

        // --- No providers ---

        [Fact]
        public void CheckEligibility_LoungeHasNoProviders_ReturnsNotEligible()
        {
            var lounge = Lounge.Create(1, "Empty Lounge");
            var membership = Membership(programId: 1);

            var result = LoungeEligibilityService.CheckEligibility(
                lounge, [membership], [], []);

            Assert.False(result.IsEligible);
            Assert.Null(result.Via);
        }

        // --- Existing membership field tests ---

        [Fact]
        public void TravelerLoyaltyMembership_Create_SetsFields()
        {
            var travelerId = Guid.NewGuid();
            var membership = TravelerLoyaltyMembership.Create(travelerId, 1, "ABC123", 5,
                new DateOnly(2026, 12, 31), null);

            Assert.Equal(travelerId, membership.TravelerId);
            Assert.Equal(1, membership.ProgramId);
            Assert.Equal("ABC123", membership.AccountNumber);
            Assert.Equal(5, membership.TierId);
            Assert.Equal(new DateOnly(2026, 12, 31), membership.TierExpiryDate);
        }

        [Fact]
        public void TravelerLoyaltyMembership_Update_ChangesFields()
        {
            var membership = TravelerLoyaltyMembership.Create(Guid.NewGuid(), 1, "OLD", null);
            membership.Update("NEW", 3, new DateOnly(2027, 1, 1), null);

            Assert.Equal("NEW", membership.AccountNumber);
            Assert.Equal(3, membership.TierId);
        }
    }
}
