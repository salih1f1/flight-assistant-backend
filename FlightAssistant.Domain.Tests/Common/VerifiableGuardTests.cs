using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Tests.Lounges;
using FlightAssistant.Domain.Tests.Loyalty;

namespace FlightAssistant.Domain.Tests.Common
{
    /// <summary>
    /// The argument guards on <see cref="IVerifiable.Verify(string, string, DateTime)"/> and
    /// <see cref="IVerifiable.MarkVerificationFailed"/> (#367, deferred from #355), for all 8
    /// verifiable entities. The rows come from the existing per-implementation factory lists, so an
    /// entity added to any of them is covered here too.
    ///
    /// <para>
    /// Every rejection test starts from a state the rejected call would visibly change and asserts
    /// that state survives, so a guard that ran after the assignments would fail here.
    /// </para>
    /// </summary>
    public class VerifiableGuardTests
    {
        public static IEnumerable<object[]> Factories() =>
            SeedableEntityVerificationTests.Factories()
                .Concat(LoungeVerificationTests.Factories())
                .Concat(LoyaltyVerificationTests.Factories());

        public static IEnumerable<object?[]> FactoriesWithBlankText() => WithEach(null, "", "   ");

        public static IEnumerable<object?[]> FactoriesWithNonUtcKind() => WithEach(DateTimeKind.Local, DateTimeKind.Unspecified);

        private static IEnumerable<object?[]> WithEach(params object?[] values) =>
            from factory in Factories()
            from value in values
            select new[] { factory[0], factory[1], value };

        [Fact]
        public void Factories_CoverAllEightVerifiableEntities()
        {
            Assert.Equal(8, Factories().Select(f => (string)f[0]).Distinct().Count());
        }

        // ── Verify ──────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(FactoriesWithBlankText))]
        public void Verify_BlankVerifiedBy_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create, string? blank)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartNeedingAttention(create);

            var ex = Assert.ThrowsAny<ArgumentException>(() => entity.Verify(blank!, VerificationSources.Manual));

            Assert.Equal("verifiedBy", ex.ParamName);
            AssertStillNeedsAttention(entity);
        }

        [Theory]
        [MemberData(nameof(FactoriesWithBlankText))]
        public void Verify_BlankSource_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create, string? blank)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartNeedingAttention(create);

            var ex = Assert.ThrowsAny<ArgumentException>(() => entity.Verify("admin@example.com", blank!));

            Assert.Equal("source", ex.ParamName);
            AssertStillNeedsAttention(entity);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void Verify_VerifiedByLongerThanItsColumn_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartNeedingAttention(create);
            var tooLong = new string('a', VerificationGuard.MaxVerifiedByLength + 1);

            var ex = Assert.Throws<ArgumentException>(() => entity.Verify(tooLong, VerificationSources.Manual));

            Assert.Equal("verifiedBy", ex.ParamName);
            AssertStillNeedsAttention(entity);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void Verify_SourceLongerThanItsColumn_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartNeedingAttention(create);
            var tooLong = new string('s', VerificationGuard.MaxSourceLength + 1);

            var ex = Assert.Throws<ArgumentException>(() => entity.Verify("admin@example.com", tooLong));

            Assert.Equal("source", ex.ParamName);
            AssertStillNeedsAttention(entity);
        }

        [Theory]
        [MemberData(nameof(FactoriesWithNonUtcKind))]
        public void Verify_VerifiedAtNotUtc_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create, DateTimeKind kind)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartNeedingAttention(create);
            var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, kind);

            var ex = Assert.Throws<ArgumentException>(() => entity.Verify("import", VerificationSources.Import, verifiedAt));

            Assert.Equal("verifiedAtUtc", ex.ParamName);
            AssertStillNeedsAttention(entity);
        }

        /// <summary>The limits are inclusive: values exactly at the column sizes are accepted.</summary>
        [Theory]
        [MemberData(nameof(Factories))]
        public void Verify_VerifiedByAndSourceExactlyAtTheirColumnSizes_AreAccepted(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = create();
            var verifiedBy = new string('a', VerificationGuard.MaxVerifiedByLength);
            var source = new string('s', VerificationGuard.MaxSourceLength);

            entity.Verify(verifiedBy, source);

            Assert.True(entity.IsVerified);
            Assert.Equal(verifiedBy, entity.VerifiedBy);
            Assert.Equal(source, entity.VerificationSource);
        }

        // ── MarkVerificationFailed ──────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(FactoriesWithBlankText))]
        public void MarkVerificationFailed_BlankIssue_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create, string? blank)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartVerified(create);

            var ex = Assert.ThrowsAny<ArgumentException>(() => entity.MarkVerificationFailed(blank!));

            Assert.Equal("issue", ex.ParamName);
            AssertStillVerified(entity);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void MarkVerificationFailed_IssueLongerThanItsColumn_ThrowsAndLeavesStateUnchanged(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartVerified(create);
            var tooLong = new string('i', VerificationGuard.MaxIssueLength + 1);

            var ex = Assert.Throws<ArgumentException>(() => entity.MarkVerificationFailed(tooLong));

            Assert.Equal("issue", ex.ParamName);
            AssertStillVerified(entity);
        }

        [Theory]
        [MemberData(nameof(Factories))]
        public void MarkVerificationFailed_IssueExactlyAtItsColumnSize_IsAccepted(string name, Func<IVerifiable> create)
        {
            Assert.NotEmpty(name); // theory label, asserted so the parameter isn't flagged unused
            var entity = StartVerified(create);
            var issue = new string('i', VerificationGuard.MaxIssueLength);

            entity.MarkVerificationFailed(issue);

            Assert.False(entity.IsVerified);
            Assert.Equal(issue, entity.VerificationIssue);
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        private const string ExistingIssue = "Name differs (ourairports)";
        private const string ExistingVerifier = "prior-admin@example.com";

        private static IVerifiable StartNeedingAttention(Func<IVerifiable> create)
        {
            var entity = create();
            entity.MarkVerificationFailed(ExistingIssue);
            return entity;
        }

        private static IVerifiable StartVerified(Func<IVerifiable> create)
        {
            var entity = create();
            entity.Verify(ExistingVerifier, VerificationSources.Manual);
            return entity;
        }

        private static void AssertStillNeedsAttention(IVerifiable entity)
        {
            Assert.False(entity.IsVerified);
            Assert.Null(entity.VerifiedAt);
            Assert.Null(entity.VerifiedBy);
            Assert.Null(entity.VerificationSource);
            Assert.Equal(ExistingIssue, entity.VerificationIssue);
        }

        private static void AssertStillVerified(IVerifiable entity)
        {
            Assert.True(entity.IsVerified);
            Assert.NotNull(entity.VerifiedAt);
            Assert.Equal(ExistingVerifier, entity.VerifiedBy);
            Assert.Equal(VerificationSources.Manual, entity.VerificationSource);
            Assert.Null(entity.VerificationIssue);
        }
    }
}
