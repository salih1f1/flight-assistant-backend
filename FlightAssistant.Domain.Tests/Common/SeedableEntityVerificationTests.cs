using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Domain.Tests.Common
{
    public class SeedableEntityVerificationTests
    {
        private static Airport CreateAirport() =>
            Airport.Create("LHR", "EGLL", "Heathrow", "London", "GB");

        [Fact]
        public void DefaultState_IsNotVerified()
        {
            var airport = CreateAirport();

            Assert.False(airport.IsVerified);
            Assert.Null(airport.VerifiedAt);
            Assert.Null(airport.VerifiedBy);
        }

        [Fact]
        public void Verify_SetsVerifiedStateWithTimestampAndActor()
        {
            var airport = CreateAirport();
            var before = DateTime.UtcNow;

            airport.Verify("admin@example.com");

            Assert.True(airport.IsVerified);
            Assert.NotNull(airport.VerifiedAt);
            Assert.True(airport.VerifiedAt >= before);
            Assert.Equal("admin@example.com", airport.VerifiedBy);
        }

        [Fact]
        public void Unverify_ResetsVerifiedState()
        {
            var airport = CreateAirport();
            airport.Verify("admin@example.com");

            airport.Unverify();

            Assert.False(airport.IsVerified);
            Assert.Null(airport.VerifiedAt);
            Assert.Null(airport.VerifiedBy);
        }

        [Fact]
        public void Airport_SatisfiesIVerifiableInterface()
        {
            var airport = CreateAirport();

            Assert.IsAssignableFrom<IVerifiable>(airport);
        }
    }
}
