using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Domain.Tests.Verification
{
    /// <summary>
    /// <see cref="VerificationRequest"/>'s factory and outcome transitions (#367 Task 1). The claim
    /// (Pending to Running) is SQL-only and is covered by the PostgreSQL repository tests.
    /// </summary>
    public class VerificationRequestTests
    {
        private static readonly DateTime RequestedUtc = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime NowUtc = RequestedUtc.AddMinutes(5);

        private static VerificationRequest NewRequest(params string[] sources) =>
            VerificationRequest.Create(
                VerificationEntityType.Airport,
                "123",
                sources.Length == 0 ? new[] { "ourairports" } : sources,
                VerificationTrigger.Admin,
                "admin-oid",
                0.015m,
                RequestedUtc);

        // ── Create ──────────────────────────────────────────────────────────────

        [Fact]
        public void Create_ReturnsAnOpenPendingRequestWithNoAttempts()
        {
            var request = VerificationRequest.Create(
                VerificationEntityType.Country, "GB", new[] { "ourairports", "aeroapi" },
                VerificationTrigger.Flight, "system", 0.015m, RequestedUtc);

            Assert.Equal(VerificationEntityType.Country, request.EntityType);
            Assert.Equal("GB", request.EntityKey);
            Assert.Equal("ourairports,aeroapi", request.Sources);
            Assert.Equal(new[] { "ourairports", "aeroapi" }, request.SourceNames);
            Assert.Equal(VerificationTrigger.Flight, request.Trigger);
            Assert.Equal(VerificationRequestStatus.Pending, request.Status);
            Assert.True(request.IsOpen);
            Assert.Equal(0, request.Attempts);
            Assert.Equal(0.015m, request.EstimatedCostUsd);
            Assert.Equal("system", request.RequestedBy);
            Assert.Equal(RequestedUtc, request.RequestedUtc);
            Assert.Null(request.NextAttemptUtc);
            Assert.Null(request.StartedUtc);
            Assert.Null(request.CompletedUtc);
            Assert.Null(request.DifferencesJson);
            Assert.Null(request.SourceValuesJson);
            Assert.Null(request.Error);
            Assert.Null(request.FailedSource);
        }

        /// <summary>Goes through <see cref="VerificationRequest.TryJoinSources"/>, the rule the job's
        /// configured default sources also use (<c>VerificationOptionsTests</c> accepts the same name).</summary>
        [Fact]
        public void Create_SourceNamesWithDigitsHyphensAndUnderscores_AreAccepted()
        {
            var request = NewRequest("aero-api_2", "ourairports");

            Assert.Equal("aero-api_2,ourairports", request.Sources);
        }

        [Fact]
        public void Create_RepeatedSourceName_IsStoredOnce()
        {
            var request = NewRequest("ourairports", "aeroapi", "ourairports");

            Assert.Equal("ourairports,aeroapi", request.Sources);
        }

        public static IEnumerable<object[]> InvalidCreateArguments()
        {
            object[] Case(string label, string paramName, Action create) => new object[] { label, paramName, create };
            var sources = new[] { "ourairports" };
            const VerificationEntityType airport = VerificationEntityType.Airport;
            const VerificationTrigger admin = VerificationTrigger.Admin;

            yield return Case("blank key", "entityKey", () => VerificationRequest.Create(airport, " ", sources, admin, "a", 0m, RequestedUtc));
            yield return Case("no sources", "sources", () => VerificationRequest.Create(airport, "1", Array.Empty<string>(), admin, "a", 0m, RequestedUtc));
            yield return Case("blank source", "sources", () => VerificationRequest.Create(airport, "1", new[] { " " }, admin, "a", 0m, RequestedUtc));
            yield return Case("source with comma", "sources", () => VerificationRequest.Create(airport, "1", new[] { "a,b" }, admin, "a", 0m, RequestedUtc));
            yield return Case("source with plus", "sources", () => VerificationRequest.Create(airport, "1", new[] { "a+b" }, admin, "a", 0m, RequestedUtc));
            yield return Case("source with space", "sources", () => VerificationRequest.Create(airport, "1", new[] { "our airports" }, admin, "a", 0m, RequestedUtc));
            yield return Case("sources over 100", "sources", () => VerificationRequest.Create(airport, "1", new[] { new string('s', 60), new string('t', 40) }, admin, "a", 0m, RequestedUtc));
            yield return Case("blank requestedBy", "requestedBy", () => VerificationRequest.Create(airport, "1", sources, admin, "", 0m, RequestedUtc));
            yield return Case("requestedBy over 255", "requestedBy", () => VerificationRequest.Create(airport, "1", sources, admin, new string('r', 256), 0m, RequestedUtc));
            yield return Case("negative cost", "estimatedCostUsd", () => VerificationRequest.Create(airport, "1", sources, admin, "a", -0.01m, RequestedUtc));
            yield return Case("requestedUtc not UTC", "requestedUtc", () => VerificationRequest.Create(airport, "1", sources, admin, "a", 0m, DateTime.SpecifyKind(RequestedUtc, DateTimeKind.Unspecified)));
            yield return Case("undefined entity type", "entityType", () => VerificationRequest.Create((VerificationEntityType)99, "1", sources, admin, "a", 0m, RequestedUtc));
            yield return Case("undefined trigger", "trigger", () => VerificationRequest.Create(airport, "1", sources, (VerificationTrigger)99, "a", 0m, RequestedUtc));
        }

        [Theory]
        [MemberData(nameof(InvalidCreateArguments))]
        public void Create_InvalidArgument_ThrowsNamingIt(string label, string paramName, Action create)
        {
            Assert.NotEmpty(label); // theory label, asserted so the parameter isn't flagged unused

            var ex = Assert.ThrowsAny<ArgumentException>(create);

            Assert.Equal(paramName, ex.ParamName);
        }

        // ── Entity key canonical form ───────────────────────────────────────────

        [Theory]
        [InlineData(VerificationEntityType.Country, "GB", "GB")]
        [InlineData(VerificationEntityType.Country, "gb", "GB")]
        [InlineData(VerificationEntityType.Country, "Gb", "GB")]
        [InlineData(VerificationEntityType.Airport, "1", "1")]
        [InlineData(VerificationEntityType.Airport, "42", "42")]
        [InlineData(VerificationEntityType.Airline, "2147483647", "2147483647")]
        public void Create_EntityKey_IsStoredInCanonicalForm(VerificationEntityType entityType, string key, string expected)
        {
            var request = VerificationRequest.Create(entityType, key, new[] { "ourairports" }, VerificationTrigger.Admin, "a", 0m, RequestedUtc);

            Assert.Equal(expected, request.EntityKey);
        }

        [Theory]
        // Empty, or surrounding whitespace: rejected, never trimmed.
        [InlineData(VerificationEntityType.Country, "")]
        [InlineData(VerificationEntityType.Airport, "")]
        [InlineData(VerificationEntityType.Country, " GB")]
        [InlineData(VerificationEntityType.Country, "GB ")]
        [InlineData(VerificationEntityType.Airport, " 42")]
        [InlineData(VerificationEntityType.Airline, "42\t")]
        // Country: two letters after upper-casing.
        [InlineData(VerificationEntityType.Country, "GBR")]
        [InlineData(VerificationEntityType.Country, "G")]
        [InlineData(VerificationEntityType.Country, "G1")]
        [InlineData(VerificationEntityType.Country, "ÅB")]
        [InlineData(VerificationEntityType.Country, "G B")]
        // Airport, Airline: positive Int32 in canonical decimal.
        [InlineData(VerificationEntityType.Airport, "0")]
        [InlineData(VerificationEntityType.Airport, "042")]
        [InlineData(VerificationEntityType.Airport, "-1")]
        [InlineData(VerificationEntityType.Airport, "+1")]
        [InlineData(VerificationEntityType.Airline, "1.0")]
        [InlineData(VerificationEntityType.Airline, "1 2")]
        [InlineData(VerificationEntityType.Airline, "LHR")]
        [InlineData(VerificationEntityType.Airport, "2147483648")]
        [InlineData(VerificationEntityType.Airport, "٤٢")] // Arabic-Indic digits "42"
        public void Create_NonCanonicalEntityKey_Throws(VerificationEntityType entityType, string key)
        {
            var ex = Assert.ThrowsAny<ArgumentException>(() =>
                VerificationRequest.Create(entityType, key, new[] { "ourairports" }, VerificationTrigger.Admin, "a", 0m, RequestedUtc));

            Assert.Equal("entityKey", ex.ParamName);
        }

        [Theory]
        [InlineData(VerificationEntityType.Country, "gb", "GB")]
        [InlineData(VerificationEntityType.Airport, "42", "42")]
        [InlineData(VerificationEntityType.Airline, "2147483647", "2147483647")]
        public void TryCanonicalEntityKey_AValidKey_ReturnsTheSameCanonicalFormCreateStores(
            VerificationEntityType entityType, string key, string expected)
        {
            Assert.True(VerificationRequest.TryCanonicalEntityKey(entityType, key, out var canonical, out var error));

            Assert.Equal(expected, canonical);
            Assert.Null(error);
            Assert.Equal(canonical, VerificationRequest.Create(entityType, key, new[] { "s" }, VerificationTrigger.Admin, "a", 0m, RequestedUtc).EntityKey);
        }

        [Theory]
        [InlineData(VerificationEntityType.Country, null)]
        [InlineData(VerificationEntityType.Country, "")]
        [InlineData(VerificationEntityType.Country, "GB ")]
        [InlineData(VerificationEntityType.Country, "GBR")]
        [InlineData(VerificationEntityType.Airport, "042")]
        [InlineData(VerificationEntityType.Airport, "2147483648")]
        [InlineData(VerificationEntityType.Airline, "LHR")]
        [InlineData((VerificationEntityType)99, "1")]
        public void TryCanonicalEntityKey_AMalformedKey_ReturnsFalseWithAReason(VerificationEntityType entityType, string? key)
        {
            Assert.False(VerificationRequest.TryCanonicalEntityKey(entityType, key, out var canonical, out var error));

            Assert.Null(canonical);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Theory]
        [InlineData(VerificationEntityType.Airport, "", "Key cannot be blank.")]
        [InlineData(VerificationEntityType.Country, null, "Key cannot be blank.")]
        [InlineData(VerificationEntityType.Airport, " 42", "Key ' 42' has surrounding whitespace.")]
        [InlineData(VerificationEntityType.Country, "GBR", "A Country key must be a two-letter code; got 'GBR'.")]
        [InlineData(VerificationEntityType.Airline, "042", "An Airline key must be a positive integer id with no sign or leading zeros; got '042'.")]
        public void TryCanonicalEntityKey_TheReason_IsReadableAndNamesNoParameter(
            VerificationEntityType entityType, string? key, string expected)
        {
            Assert.False(VerificationRequest.TryCanonicalEntityKey(entityType, key, out _, out var error));

            Assert.Equal(expected, error);
        }

        [Fact]
        public void Create_NullEntityKey_StillThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                VerificationRequest.Create(VerificationEntityType.Airport, null!, new[] { "s" }, VerificationTrigger.Admin, "a", 0m, RequestedUtc));
        }

        [Fact]
        public void Create_SourcesExactly100CharactersJoined_IsAccepted()
        {
            var request = NewRequest(new string('s', 60), new string('t', 39)); // 60 + 1 separator + 39

            Assert.Equal(100, request.Sources.Length);
        }

        // ── Outcomes ────────────────────────────────────────────────────────────

        [Fact]
        public void MarkVerified_CompletesAsVerified()
        {
            var request = NewRequest();

            request.MarkVerified(NowUtc);

            Assert.Equal(VerificationRequestStatus.Verified, request.Status);
            Assert.False(request.IsOpen);
            Assert.Equal(NowUtc, request.CompletedUtc);
            Assert.Null(request.NextAttemptUtc);
            Assert.Null(request.Error);
            Assert.Null(request.FailedSource);
        }

        [Fact]
        public void MarkVerified_AfterAnEarlierSourceError_ClearsTheErrorAndNextAttempt()
        {
            var request = NewRequest();
            request.RecordSourceError("timeout", RequestedUtc, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1));

            request.MarkVerified(NowUtc);

            Assert.Equal(VerificationRequestStatus.Verified, request.Status);
            Assert.Equal(1, request.Attempts);
            Assert.Null(request.Error);
            Assert.Null(request.NextAttemptUtc);
        }

        [Fact]
        public void MarkMismatch_CompletesAsMismatch_WithDifferencesAndSourceValues()
        {
            var request = NewRequest();
            const string differences = """[{"column":"Name","ours":"Heathrow","source":"London Heathrow","sourceName":"ourairports"}]""";
            const string sourceValues = """{"ourairports":{"Name":"London Heathrow"}}""";

            request.MarkMismatch(differences, sourceValues, NowUtc);

            Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);
            Assert.Equal(differences, request.DifferencesJson);
            Assert.Equal(sourceValues, request.SourceValuesJson);
            Assert.Equal(NowUtc, request.CompletedUtc);
            Assert.Null(request.NextAttemptUtc);
            Assert.Null(request.Error);
            Assert.Null(request.FailedSource);
        }

        [Fact]
        public void MarkNotFound_CompletesAsNotFound_WithSourceValuesOnly()
        {
            var request = NewRequest();

            request.MarkNotFound("{}", NowUtc);

            Assert.Equal(VerificationRequestStatus.NotFound, request.Status);
            Assert.Equal("{}", request.SourceValuesJson);
            Assert.Null(request.DifferencesJson);
            Assert.Equal(NowUtc, request.CompletedUtc);
            Assert.Null(request.Error);
            Assert.Null(request.FailedSource);
        }

        [Fact]
        public void MarkError_CompletesAsErrorWithTheMessage_WithoutCountingAnAttempt()
        {
            var request = NewRequest();

            request.MarkError("Entity no longer exists", NowUtc);

            Assert.Equal(VerificationRequestStatus.Error, request.Status);
            Assert.Equal("Entity no longer exists", request.Error);
            Assert.Equal(0, request.Attempts);
            Assert.Equal(NowUtc, request.CompletedUtc);
            Assert.Null(request.NextAttemptUtc);
            Assert.Null(request.FailedSource);
        }

        [Fact]
        public void MarkError_LongError_IsTruncatedTo1000Characters()
        {
            var request = NewRequest();

            request.MarkError(new string('e', 1500), NowUtc);

            Assert.Equal(new string('e', VerificationRequest.MaxErrorLength), request.Error);
        }

        [Fact]
        public void MarkError_TruncationPoint_InsideASurrogatePair_DropsTheWholePair()
        {
            var request = NewRequest();
            var error = new string('e', VerificationRequest.MaxErrorLength - 1) + "\U0001F6EB" + "tail"; // emoji at 999–1000

            request.MarkError(error, NowUtc);

            Assert.Equal(new string('e', VerificationRequest.MaxErrorLength - 1), request.Error);
        }

        // ── The stuck-request reset's abandoned-claim error ─────────────────────

        [Fact]
        public void ClaimAbandonedError_NamesTheAttemptCount_FromItsPrefixAndSuffix()
        {
            Assert.Equal("Claim abandoned: no result recorded after 3 attempts", VerificationRequest.ClaimAbandonedError(3));
            Assert.Equal(
                VerificationRequest.ClaimAbandonedErrorPrefix + "10" + VerificationRequest.ClaimAbandonedErrorSuffix,
                VerificationRequest.ClaimAbandonedError(10));
        }

        // ── RecordSourceError ───────────────────────────────────────────────────

        [Fact]
        public void RecordSourceError_BelowMaxAttempts_ReturnsToPendingWithNextAttemptAfterTheBackoff()
        {
            var request = NewRequest();
            var firstFailure = RequestedUtc.AddMinutes(1);
            var secondFailure = RequestedUtc.AddMinutes(3);

            request.RecordSourceError("HTTP 503", firstFailure, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1));

            Assert.Equal(VerificationRequestStatus.Pending, request.Status);
            Assert.True(request.IsOpen);
            Assert.Equal(1, request.Attempts);
            Assert.Equal(firstFailure.AddMinutes(1), request.NextAttemptUtc);
            Assert.Equal("HTTP 503", request.Error);
            Assert.Null(request.FailedSource); // no source given
            Assert.Null(request.StartedUtc);
            Assert.Null(request.CompletedUtc);

            request.RecordSourceError("timeout", secondFailure, maxAttempts: 3, backoff: TimeSpan.FromMinutes(5));

            Assert.Equal(VerificationRequestStatus.Pending, request.Status);
            Assert.Equal(2, request.Attempts);
            Assert.Equal(secondFailure.AddMinutes(5), request.NextAttemptUtc);
            Assert.Equal("timeout", request.Error);
            Assert.Null(request.FailedSource);
            Assert.Null(request.CompletedUtc);
        }

        [Fact]
        public void RecordSourceError_ReachingMaxAttempts_CompletesAsError()
        {
            var request = NewRequest();
            request.RecordSourceError("HTTP 503", RequestedUtc.AddMinutes(1), maxAttempts: 3, backoff: TimeSpan.FromMinutes(1));
            request.RecordSourceError("HTTP 503", RequestedUtc.AddMinutes(3), maxAttempts: 3, backoff: TimeSpan.FromMinutes(5));
            var thirdFailure = RequestedUtc.AddMinutes(9);

            request.RecordSourceError("HTTP 429", thirdFailure, maxAttempts: 3, backoff: TimeSpan.FromMinutes(5));

            Assert.Equal(VerificationRequestStatus.Error, request.Status);
            Assert.False(request.IsOpen);
            Assert.Equal(3, request.Attempts);
            Assert.Equal("HTTP 429", request.Error);
            Assert.Equal(thirdFailure, request.CompletedUtc);
            Assert.Null(request.NextAttemptUtc);
        }

        [Fact]
        public void RecordSourceError_WithMaxAttemptsOfOne_FirstFailureCompletesAsError()
        {
            var request = NewRequest();

            request.RecordSourceError("timeout", NowUtc, maxAttempts: 1, backoff: TimeSpan.FromMinutes(1));

            Assert.Equal(VerificationRequestStatus.Error, request.Status);
            Assert.Equal(1, request.Attempts);
            Assert.Equal(NowUtc, request.CompletedUtc);
        }

        [Fact]
        public void RecordSourceError_LongError_IsTruncatedTo1000Characters()
        {
            var request = NewRequest();

            request.RecordSourceError(new string('e', 1001), NowUtc, maxAttempts: 3, backoff: TimeSpan.Zero);

            Assert.Equal(VerificationRequest.MaxErrorLength, request.Error!.Length);
        }

        [Fact]
        public void RecordSourceError_InvalidLimits_Throw()
        {
            var request = NewRequest();

            Assert.Throws<ArgumentOutOfRangeException>(() => request.RecordSourceError("x", NowUtc, maxAttempts: 0, backoff: TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => request.RecordSourceError("x", NowUtc, maxAttempts: 3, backoff: TimeSpan.FromSeconds(-1)));
            Assert.Equal(0, request.Attempts);
        }

        // ── FailedSource (#424): non-null iff Error is attributable to one named source, and it
        // changes exactly when Error changes ───────────────────────────────────

        [Fact]
        public void RecordSourceError_WithASource_SetsFailedSourceOnARetry()
        {
            var request = NewRequest();

            request.RecordSourceError("HTTP 503", NowUtc, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1), failedSource: "aeroapi");

            Assert.Equal(VerificationRequestStatus.Pending, request.Status);
            Assert.Equal("aeroapi", request.FailedSource);
        }

        [Fact]
        public void RecordSourceError_WithASource_StillSetsFailedSourceWhenItCompletesAsError()
        {
            var request = NewRequest();
            request.RecordSourceError("HTTP 503", RequestedUtc.AddMinutes(1), maxAttempts: 2, backoff: TimeSpan.FromMinutes(1), failedSource: "aeroapi");

            request.RecordSourceError("HTTP 429", RequestedUtc.AddMinutes(3), maxAttempts: 2, backoff: TimeSpan.FromMinutes(1), failedSource: "aeroapi");

            Assert.Equal(VerificationRequestStatus.Error, request.Status);
            Assert.Equal("aeroapi", request.FailedSource);
        }

        [Fact]
        public void RecordSourceError_WithNoSource_LeavesFailedSourceNull()
        {
            var request = NewRequest();

            request.RecordSourceError("Unexpected error (TimeoutException); see logs", NowUtc, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1));

            Assert.Null(request.FailedSource);
            Assert.Equal("Unexpected error (TimeoutException); see logs", request.Error);
        }

        [Fact]
        public void RecordSourceError_ANewSourceOnARetry_ReplacesTheEarlierOne()
        {
            var request = NewRequest();
            request.RecordSourceError("HTTP 503", RequestedUtc.AddMinutes(1), maxAttempts: 3, backoff: TimeSpan.FromMinutes(1), failedSource: "aeroapi");
            Assert.Equal("aeroapi", request.FailedSource);

            request.RecordSourceError("Source 'ourairports' is not registered.", RequestedUtc.AddMinutes(3), maxAttempts: 3, backoff: TimeSpan.FromMinutes(1), failedSource: "ourairports");

            Assert.Equal("ourairports", request.FailedSource);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("aero api")]
        [InlineData("aero,api")]
        [InlineData("aero+api")]
        public void RecordSourceError_InvalidFailedSource_ThrowsNamingTheParameterAndChangesNothing(string failedSource)
        {
            var request = NewRequest();

            var ex = Assert.Throws<ArgumentException>(() =>
                request.RecordSourceError("timeout", NowUtc, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1), failedSource: failedSource));

            Assert.Equal("failedSource", ex.ParamName);
            Assert.Equal(0, request.Attempts);
            Assert.Null(request.Error);
            Assert.Null(request.FailedSource);
        }

        [Fact]
        public void RecordSourceError_FailedSourceOverTheLimit_Throws()
        {
            var request = NewRequest();

            var ex = Assert.Throws<ArgumentException>(() =>
                request.RecordSourceError("timeout", NowUtc, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1),
                    failedSource: new string('s', VerificationRequest.MaxFailedSourceLength + 1)));

            Assert.Equal("failedSource", ex.ParamName);
        }

        [Fact]
        public void MarkError_WithASource_SetsFailedSource()
        {
            var request = NewRequest();

            request.MarkError("aeroapi: HTTP 503", NowUtc, failedSource: "aeroapi");

            Assert.Equal(VerificationRequestStatus.Error, request.Status);
            Assert.Equal("aeroapi", request.FailedSource);
        }

        [Fact]
        public void MarkError_WithNoSource_LeavesFailedSourceNull()
        {
            var request = NewRequest();

            request.MarkError("Entity no longer exists", NowUtc);

            Assert.Null(request.FailedSource);
        }

        [Fact]
        public void MarkError_FailedSourceExactlyAtTheLimit_IsAccepted()
        {
            var request = NewRequest();
            var name = new string('s', VerificationRequest.MaxFailedSourceLength);

            request.MarkError("boom", NowUtc, failedSource: name);

            Assert.Equal(name, request.FailedSource);
        }

        [Fact]
        public void MarkError_FailedSourceOverTheLimit_Throws()
        {
            var request = NewRequest();

            var ex = Assert.Throws<ArgumentException>(() =>
                request.MarkError("boom", NowUtc, failedSource: new string('s', VerificationRequest.MaxFailedSourceLength + 1)));

            Assert.Equal("failedSource", ex.ParamName);
        }

        public static IEnumerable<object[]> SuccessOutcomes()
        {
            object[] Case(string label, Action<VerificationRequest, DateTime> apply) => new object[] { label, apply };

            yield return Case(nameof(VerificationRequest.MarkVerified), (r, now) => r.MarkVerified(now));
            yield return Case(nameof(VerificationRequest.MarkMismatch), (r, now) => r.MarkMismatch("[]", "{}", now));
            yield return Case(nameof(VerificationRequest.MarkNotFound), (r, now) => r.MarkNotFound("{}", now));
        }

        /// <summary>The lifecycle invariant's other half: a request that failed with an attributed
        /// source, then later succeeds, clears FailedSource exactly as it clears Error. Break-proof:
        /// deleting the "FailedSource = null;" line from any of the three success methods fails this
        /// for that method's case.</summary>
        [Theory]
        [MemberData(nameof(SuccessOutcomes))]
        public void SuccessOutcome_AfterASourceErrorNamedASource_ClearsFailedSource(string label, Action<VerificationRequest, DateTime> apply)
        {
            Assert.NotEmpty(label); // theory label, asserted so the parameter isn't flagged unused
            var request = NewRequest();
            request.RecordSourceError("HTTP 503", RequestedUtc, maxAttempts: 3, backoff: TimeSpan.FromMinutes(1), failedSource: "aeroapi");
            Assert.Equal("aeroapi", request.FailedSource); // sanity check before the outcome clears it

            apply(request, NowUtc);

            Assert.Null(request.FailedSource);
            Assert.Null(request.Error);
        }

        // ── Guards shared by every outcome ──────────────────────────────────────

        public static IEnumerable<object[]> Outcomes()
        {
            object[] Case(string label, Action<VerificationRequest, DateTime> apply) => new object[] { label, apply };

            yield return Case(nameof(VerificationRequest.MarkVerified), (r, now) => r.MarkVerified(now));
            yield return Case(nameof(VerificationRequest.MarkMismatch), (r, now) => r.MarkMismatch("[]", "{}", now));
            yield return Case(nameof(VerificationRequest.MarkNotFound), (r, now) => r.MarkNotFound("{}", now));
            yield return Case(nameof(VerificationRequest.RecordSourceError), (r, now) => r.RecordSourceError("timeout", now, 3, TimeSpan.FromMinutes(1)));
            yield return Case(nameof(VerificationRequest.MarkError), (r, now) => r.MarkError("boom", now));
        }

        [Theory]
        [MemberData(nameof(Outcomes))]
        public void Outcome_OnACompletedRequest_ThrowsAndKeepsTheRecordedOutcome(string label, Action<VerificationRequest, DateTime> apply)
        {
            Assert.NotEmpty(label); // theory label, asserted so the parameter isn't flagged unused
            var request = NewRequest();
            request.MarkVerified(NowUtc);

            Assert.Throws<InvalidOperationException>(() => apply(request, NowUtc.AddMinutes(1)));

            Assert.Equal(VerificationRequestStatus.Verified, request.Status);
            Assert.Equal(NowUtc, request.CompletedUtc);
        }

        [Theory]
        [MemberData(nameof(Outcomes))]
        public void Outcome_WithANonUtcNow_ThrowsAndLeavesTheRequestPending(string label, Action<VerificationRequest, DateTime> apply)
        {
            Assert.NotEmpty(label); // theory label, asserted so the parameter isn't flagged unused
            var request = NewRequest();

            var ex = Assert.Throws<ArgumentException>(() => apply(request, DateTime.SpecifyKind(NowUtc, DateTimeKind.Local)));

            Assert.Equal("nowUtc", ex.ParamName);
            Assert.Equal(VerificationRequestStatus.Pending, request.Status);
            Assert.Null(request.CompletedUtc);
        }
    }
}
