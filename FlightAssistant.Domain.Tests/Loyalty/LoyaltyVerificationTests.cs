using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Domain.Tests.Loyalty
{
    /// <summary>
    /// <see cref="IVerifiable"/> behaviour (#355 Task 1) for <see cref="LoyaltyProgram"/> and
    /// <see cref="LoyaltyProgramKind"/>, which each implement <see cref="IVerifiable"/> directly
    /// (they are not <c>SeedableEntity</c> subtypes). One theory over a factory per entity rather
    /// than two near-identical copies of the same test.
    /// </summary>
    public class LoyaltyVerificationTests
    {
        public static IEnumerable<object[]> Factories()
        {
            yield return new object[] { "LoyaltyProgram", (Func<IVerifiable>)(() => LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1)) };
            yield return new object[] { "LoyaltyProgramKind", (Func<IVerifiable>)(() => LoyaltyProgramKind.Create("AIRLINE", "Airline")) };
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void DefaultState_IsNotVerified(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();

            Assert.False(entity.IsVerified);
            Assert.Null(entity.VerifiedAt);
            Assert.Null(entity.VerifiedBy);
            Assert.Null(entity.VerificationSource);
            Assert.Null(entity.VerificationIssue);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void Verify_SetsVerifiedStateSourceAndClearsIssue(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();
            entity.MarkVerificationFailed("stale seed data"); // start from Needs attention
            var before = DateTime.UtcNow;

            entity.Verify("admin@example.com", VerificationSources.Manual);

            Assert.True(entity.IsVerified);
            Assert.NotNull(entity.VerifiedAt);
            Assert.True(entity.VerifiedAt >= before);
            Assert.Equal("admin@example.com", entity.VerifiedBy);
            Assert.Equal(VerificationSources.Manual, entity.VerificationSource);
            Assert.Null(entity.VerificationIssue);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void Unverify_ClearsAllFiveVerificationProperties(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();
            entity.Verify("admin@example.com", VerificationSources.Manual);

            entity.Unverify();

            Assert.False(entity.IsVerified);
            Assert.Null(entity.VerifiedAt);
            Assert.Null(entity.VerifiedBy);
            Assert.Null(entity.VerificationSource);
            Assert.Null(entity.VerificationIssue);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void MarkVerificationFailed_OnVerifiedEntity_ClearsVerifiedStateAndSetsIssue(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();
            entity.Verify("admin@example.com", VerificationSources.Manual);

            entity.MarkVerificationFailed("Name, City differ (ourairports)");

            Assert.False(entity.IsVerified);
            Assert.Null(entity.VerifiedAt);
            Assert.Null(entity.VerifiedBy);
            Assert.Null(entity.VerificationSource);
            Assert.Equal("Name, City differ (ourairports)", entity.VerificationIssue);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void SatisfiesIVerifiableInterface(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            Assert.IsAssignableFrom<IVerifiable>(create());
        }
    }
}
