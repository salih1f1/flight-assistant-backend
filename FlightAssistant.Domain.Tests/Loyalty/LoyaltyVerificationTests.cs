using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Domain.Tests.Loyalty
{
    public class LoyaltyVerificationTests
    {
        // --- LoyaltyProgram ---

        [Fact]
        public void LoyaltyProgram_DefaultState_IsNotVerified()
        {
            var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1);

            Assert.False(program.IsVerified);
            Assert.Null(program.VerifiedAt);
            Assert.Null(program.VerifiedBy);
        }

        [Fact]
        public void LoyaltyProgram_Verify_SetsVerifiedStateWithTimestampAndActor()
        {
            var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1);
            var before = DateTime.UtcNow;

            program.Verify("admin@example.com");

            Assert.True(program.IsVerified);
            Assert.NotNull(program.VerifiedAt);
            Assert.True(program.VerifiedAt >= before);
            Assert.Equal("admin@example.com", program.VerifiedBy);
        }

        [Fact]
        public void LoyaltyProgram_Unverify_ResetsVerifiedState()
        {
            var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1);
            program.Verify("admin@example.com");

            program.Unverify();

            Assert.False(program.IsVerified);
            Assert.Null(program.VerifiedAt);
            Assert.Null(program.VerifiedBy);
        }

        [Fact]
        public void LoyaltyProgram_SatisfiesIVerifiableInterface()
        {
            var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1);

            Assert.IsAssignableFrom<IVerifiable>(program);
        }

        // --- LoyaltyProgramKind ---

        [Fact]
        public void LoyaltyProgramKind_DefaultState_IsNotVerified()
        {
            var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline");

            Assert.False(kind.IsVerified);
            Assert.Null(kind.VerifiedAt);
            Assert.Null(kind.VerifiedBy);
        }

        [Fact]
        public void LoyaltyProgramKind_Verify_SetsVerifiedStateWithTimestampAndActor()
        {
            var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline");
            var before = DateTime.UtcNow;

            kind.Verify("admin@example.com");

            Assert.True(kind.IsVerified);
            Assert.NotNull(kind.VerifiedAt);
            Assert.True(kind.VerifiedAt >= before);
            Assert.Equal("admin@example.com", kind.VerifiedBy);
        }

        [Fact]
        public void LoyaltyProgramKind_Unverify_ResetsVerifiedState()
        {
            var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline");
            kind.Verify("admin@example.com");

            kind.Unverify();

            Assert.False(kind.IsVerified);
            Assert.Null(kind.VerifiedAt);
            Assert.Null(kind.VerifiedBy);
        }

        [Fact]
        public void LoyaltyProgramKind_SatisfiesIVerifiableInterface()
        {
            var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline");

            Assert.IsAssignableFrom<IVerifiable>(kind);
        }
    }
}
