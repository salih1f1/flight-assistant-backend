using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Domain.Tests.Common
{
    /// <summary>
    /// <see cref="IVerifiable"/> behaviour (#355 Task 1) for the four <see cref="SeedableEntity"/>
    /// subtypes — Airport, Airline, Alliance, Country — which all inherit the same implementation
    /// from <see cref="SeedableEntity"/> itself. One theory over a factory per entity rather than
    /// four near-identical copies of the same test.
    /// </summary>
    public class SeedableEntityVerificationTests
    {
        public static IEnumerable<object[]> Factories()
        {
            yield return new object[] { "Airport", (Func<IVerifiable>)(() => Airport.Create("LHR", "EGLL", "Heathrow", "London", "GB")) };
            yield return new object[] { "Airline", (Func<IVerifiable>)(() => Airline.Create("BA", "BAW", "British Airways", "GB")) };
            yield return new object[] { "Alliance", (Func<IVerifiable>)(() => Alliance.Create("SA", "Test Alliance")) };
            yield return new object[] { "Country", (Func<IVerifiable>)(() => Country.Create("GB", "United Kingdom")) };
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

        /// <summary>
        /// The three-argument overload (#362), used by CSV import to keep a row's own
        /// <c>VerifiedAt</c> from the file rather than restamping the moment of import.
        /// </summary>
        [Theory]
        [MemberData(nameof(Factories))]
        public void Verify_WithExplicitVerifiedAtUtc_SetsAllFourAndClearsIssue(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();
            entity.MarkVerificationFailed("stale seed data"); // start from Needs attention
            var explicitVerifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            entity.Verify("import", VerificationSources.Import, explicitVerifiedAt);

            Assert.True(entity.IsVerified);
            Assert.Equal(explicitVerifiedAt, entity.VerifiedAt);
            Assert.Equal("import", entity.VerifiedBy);
            Assert.Equal(VerificationSources.Import, entity.VerificationSource);
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

        /// <summary>
        /// The counterpart to <see cref="Unverify_ClearsAllFiveVerificationProperties"/>: that test
        /// seeds via <c>Verify</c>, which already leaves <c>VerificationIssue</c> null, so it could
        /// never catch <c>Unverify</c> failing to clear that one property. This seeds from Needs
        /// attention (<c>VerificationIssue</c> set, <c>VerificationSource</c> already null) instead,
        /// so clearing the issue is the thing actually being exercised.
        /// </summary>
        [Theory]
        [MemberData(nameof(Factories))]
        public void Unverify_FromNeedsAttention_ClearsVerificationIssue(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();
            entity.MarkVerificationFailed("stale seed data");

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

        /// <summary>
        /// #385 final review G1: <c>VerificationNeedsAttentionRepository</c>'s "needs attention" count
        /// and the admin data browser's <c>VerificationIssue:*</c> "has a value" filter (spec §4.7)
        /// select the same rows only because this invariant holds -- the count's predicate checks
        /// both <c>IsVerified</c> and <c>VerificationIssue</c>, but the filter is a general-purpose
        /// column filter with no knowledge of <c>IsVerified</c>, so it checks only the issue. Pins the
        /// invariant directly, across all three mutators, rather than leaving it to be inferred from
        /// the individual property assertions the tests above already make. <c>Verify</c> is the
        /// interesting case: it must actively clear a pre-existing issue, not merely not set one, so
        /// each scenario below starts from a prior Needs-attention state via
        /// <see cref="MarkVerificationFailed_OnVerifiedEntity_ClearsVerifiedStateAndSetsIssue"/>'s same
        /// setup rather than a fresh, already-null-issue entity.
        /// </summary>
        [Theory]
        [MemberData(nameof(Factories))]
        public void ANonNullVerificationIssue_AlwaysImpliesNotVerified(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused

            var afterVerify = create();
            afterVerify.MarkVerificationFailed("stale seed data");
            afterVerify.Verify("admin@example.com", VerificationSources.Manual);
            AssertInvariantHolds(afterVerify);

            var afterUnverify = create();
            afterUnverify.MarkVerificationFailed("stale seed data");
            afterUnverify.Unverify();
            AssertInvariantHolds(afterUnverify);

            var afterMarkFailed = create();
            afterMarkFailed.MarkVerificationFailed("Name, City differ (ourairports)");
            AssertInvariantHolds(afterMarkFailed);

            static void AssertInvariantHolds(IVerifiable entity)
            {
                if (entity.VerificationIssue is not null)
                    Assert.False(entity.IsVerified);
            }
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
