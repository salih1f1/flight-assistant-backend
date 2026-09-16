using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Verification;

/// <summary><see cref="GetVerificationRequestsQuery"/> (#385, Task 1): an entity's most recent verification checks.</summary>
public class GetVerificationRequestsQueryTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private const string Differences = """[{"column":"Name","ours":"Heathrow","source":"London Heathrow Airport","sourceName":"ourairports"}]""";
    private const string SourceValues = """{"ourairports":{"Name":"London Heathrow Airport","City":"London"}}""";

    private readonly IVerificationRequestRepository _repository = Substitute.For<IVerificationRequestRepository>();

    private Task<IReadOnlyList<VerificationRequestResult>> Handle(string? entityType = "Airport", string? entityKey = "42", int? limit = null) =>
        new GetVerificationRequestsQueryHandler(_repository)
            .Handle(new GetVerificationRequestsQuery(entityType, entityKey, limit), CancellationToken.None);

    private static VerificationRequest Mismatch(string key = "42")
    {
        var request = VerificationRequest.Create(
            VerificationEntityType.Airport, key, new[] { "ourairports" }, VerificationTrigger.Admin, "admin-oid", 0m, T0);
        request.MarkMismatch(Differences, SourceValues, T0.AddMinutes(1));
        return request;
    }

    // ── Mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_AValidRequest_MapsEveryField_IncludingDeserialisedDifferencesAndSourceValues()
    {
        var request = Mismatch();
        _repository.GetRecentForEntityAsync(VerificationEntityType.Airport, "42", GetVerificationRequestsQuery.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns(new List<VerificationRequest> { request });

        var results = await Handle();

        var result = Assert.Single(results);
        Assert.Equal(request.Id, result.Id);
        Assert.Equal(VerificationEntityType.Airport, result.EntityType);
        Assert.Equal("42", result.EntityKey);
        Assert.Equal(new[] { "ourairports" }, result.Sources);
        Assert.Equal(VerificationTrigger.Admin, result.Trigger);
        Assert.Equal(VerificationRequestStatus.Mismatch, result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Null(result.NextAttemptUtc);
        Assert.Equal(
            new[] { new VerificationDifference("Name", "Heathrow", "London Heathrow Airport", "ourairports") },
            result.Differences);
        Assert.Equal("London Heathrow Airport", result.SourceValues["ourairports"]["Name"]);
        Assert.Null(result.Error);
        Assert.Equal(0m, result.EstimatedCostUsd);
        Assert.Equal("admin-oid", result.RequestedBy);
        Assert.Equal(T0, result.RequestedUtc);
        Assert.Null(result.StartedUtc);
        Assert.Equal(T0.AddMinutes(1), result.CompletedUtc);
    }

    /// <summary>#385, Task 2, F1: <see cref="VerificationRequestResult.Sources"/> is the parsed source
    /// name list, in request order, not the stored comma-joined string.</summary>
    [Fact]
    public async Task Handle_MultipleSources_ComesBackAsTheParsedSourceNameList_InRequestOrder()
    {
        var request = VerificationRequest.Create(
            VerificationEntityType.Airport, "42", new[] { "ourairports", "mledoze" }, VerificationTrigger.Admin, "admin-oid", 0m, T0);
        _repository.GetRecentForEntityAsync(VerificationEntityType.Airport, "42", GetVerificationRequestsQuery.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns(new List<VerificationRequest> { request });

        var result = Assert.Single(await Handle());

        Assert.Equal(new[] { "ourairports", "mledoze" }, result.Sources);
    }

    [Fact]
    public async Task Handle_NoRequestsForTheEntity_ReturnsAnEmptyList()
    {
        _repository.GetRecentForEntityAsync(VerificationEntityType.Airport, "42", GetVerificationRequestsQuery.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns(new List<VerificationRequest>());

        Assert.Empty(await Handle());
    }

    [Fact]
    public async Task Handle_APendingRequestWithNoStoredJson_MapsToEmptyDifferencesAndSourceValues()
    {
        var pending = VerificationRequest.Create(
            VerificationEntityType.Airport, "42", new[] { "ourairports" }, VerificationTrigger.Admin, "admin-oid", 0m, T0);
        _repository.GetRecentForEntityAsync(VerificationEntityType.Airport, "42", GetVerificationRequestsQuery.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns(new List<VerificationRequest> { pending });

        var result = Assert.Single(await Handle());

        Assert.Empty(result.Differences);
        Assert.Empty(result.SourceValues);
    }

    // ── Defaults and canonicalisation ──────────────────────────────────────

    [Fact]
    public async Task Handle_NoLimitGiven_PassesTheDefaultOfFiveToTheRepository()
    {
        _repository.GetRecentForEntityAsync(Arg.Any<VerificationEntityType>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<VerificationRequest>());

        await Handle(limit: null);

        await _repository.Received(1).GetRecentForEntityAsync(VerificationEntityType.Airport, "42", 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ALowerCaseCountryCode_QueriesTheRepositoryByTheUpperCaseCanonicalForm()
    {
        _repository.GetRecentForEntityAsync(Arg.Any<VerificationEntityType>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<VerificationRequest>());

        await Handle(entityType: "Country", entityKey: "gb");

        await _repository.Received(1)
            .GetRecentForEntityAsync(VerificationEntityType.Country, "GB", GetVerificationRequestsQuery.DefaultLimit, Arg.Any<CancellationToken>());
    }

    // ── Validator ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("airport")] // exact names only
    [InlineData("Lounge")]
    public void Validator_RejectsAMissingBlankOrUnknownEntityType(string? entityType)
    {
        var result = new GetVerificationRequestsQueryValidator().Validate(new GetVerificationRequestsQuery(entityType, "42", null));

        var failure = Assert.Single(result.Errors, f => f.PropertyName == nameof(GetVerificationRequestsQuery.EntityType));
        Assert.Equal(VerificationEntityTypeNames.InvalidMessage, failure.ErrorMessage);
    }

    [Fact]
    public void Validator_AMalformedEntityKey_IsRejected_NamingEntityKey()
    {
        var result = new GetVerificationRequestsQueryValidator().Validate(new GetVerificationRequestsQuery("Country", "USA", null));

        var failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetVerificationRequestsQuery.EntityKey), failure.PropertyName);
        Assert.Equal("A Country key must be a two-letter code; got 'USA'.", failure.ErrorMessage);
    }

    [Fact]
    public void Validator_ABlankEntityKey_IsRejected()
    {
        var result = new GetVerificationRequestsQueryValidator().Validate(new GetVerificationRequestsQuery("Airport", "", null));

        var failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetVerificationRequestsQuery.EntityKey), failure.PropertyName);
        Assert.Equal("Key cannot be blank.", failure.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validator_RejectsALimitOutOfRange(int limit)
    {
        var result = new GetVerificationRequestsQueryValidator().Validate(new GetVerificationRequestsQuery("Airport", "42", limit));

        Assert.Equal(nameof(GetVerificationRequestsQuery.Limit), Assert.Single(result.Errors).PropertyName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(null)]
    public void Validator_ALimitInRangeOrAbsent_IsValid(int? limit)
    {
        Assert.True(new GetVerificationRequestsQueryValidator().Validate(new GetVerificationRequestsQuery("Airport", "42", limit)).IsValid);
    }
}
