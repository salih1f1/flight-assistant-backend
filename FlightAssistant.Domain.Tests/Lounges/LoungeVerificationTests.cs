using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;

namespace FlightAssistant.Domain.Tests.Lounges
{
    public class LoungeVerificationTests
    {
        // --- Lounge ---

        [Fact]
        public void Lounge_DefaultState_IsNotVerified()
        {
            var lounge = Lounge.Create(1, "Test Lounge");

            Assert.False(lounge.IsVerified);
            Assert.Null(lounge.VerifiedAt);
            Assert.Null(lounge.VerifiedBy);
        }

        [Fact]
        public void Lounge_Verify_SetsVerifiedStateWithTimestampAndActor()
        {
            var lounge = Lounge.Create(1, "Test Lounge");
            var before = DateTime.UtcNow;

            lounge.Verify("admin@example.com");

            Assert.True(lounge.IsVerified);
            Assert.NotNull(lounge.VerifiedAt);
            Assert.True(lounge.VerifiedAt >= before);
            Assert.Equal("admin@example.com", lounge.VerifiedBy);
        }

        [Fact]
        public void Lounge_Unverify_ResetsVerifiedState()
        {
            var lounge = Lounge.Create(1, "Test Lounge");
            lounge.Verify("admin@example.com");

            lounge.Unverify();

            Assert.False(lounge.IsVerified);
            Assert.Null(lounge.VerifiedAt);
            Assert.Null(lounge.VerifiedBy);
        }

        [Fact]
        public void Lounge_SatisfiesIVerifiableInterface()
        {
            var lounge = Lounge.Create(1, "Test Lounge");

            Assert.IsAssignableFrom<IVerifiable>(lounge);
        }

        // --- LoungeProvider ---

        [Fact]
        public void LoungeProvider_DefaultState_IsNotVerified()
        {
            var provider = LoungeProvider.Create("PP", "Priority Pass", LoungeProviderKind.Contract);

            Assert.False(provider.IsVerified);
            Assert.Null(provider.VerifiedAt);
            Assert.Null(provider.VerifiedBy);
        }

        [Fact]
        public void LoungeProvider_Verify_SetsVerifiedStateWithTimestampAndActor()
        {
            var provider = LoungeProvider.Create("PP", "Priority Pass", LoungeProviderKind.Contract);
            var before = DateTime.UtcNow;

            provider.Verify("admin@example.com");

            Assert.True(provider.IsVerified);
            Assert.NotNull(provider.VerifiedAt);
            Assert.True(provider.VerifiedAt >= before);
            Assert.Equal("admin@example.com", provider.VerifiedBy);
        }

        [Fact]
        public void LoungeProvider_Unverify_ResetsVerifiedState()
        {
            var provider = LoungeProvider.Create("PP", "Priority Pass", LoungeProviderKind.Contract);
            provider.Verify("admin@example.com");

            provider.Unverify();

            Assert.False(provider.IsVerified);
            Assert.Null(provider.VerifiedAt);
            Assert.Null(provider.VerifiedBy);
        }

        [Fact]
        public void LoungeProvider_SatisfiesIVerifiableInterface()
        {
            var provider = LoungeProvider.Create("PP", "Priority Pass", LoungeProviderKind.Contract);

            Assert.IsAssignableFrom<IVerifiable>(provider);
        }
    }
}
