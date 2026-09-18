using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Application.Tests.ReferenceData;

public class ReferenceDataProvisionerTests
{
    private readonly IAirportRepository _airports = Substitute.For<IAirportRepository>();
    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();
    private readonly IVerificationSourceRegistry _registry = Substitute.For<IVerificationSourceRegistry>();
    private readonly ICountryNameResolver _countryNames = Substitute.For<ICountryNameResolver>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();

    private sealed class Options : IReferenceProvisioningOptions
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromHours(24);
    }

    // Read IVerificationJobOptions and implement every member it declares — the list below is
    // what it had when this plan was written, and the compiler will tell you if it has since
    // grown. Only DefaultSourcesFor is actually exercised here.
    private sealed class JobOptions : IVerificationJobOptions
    {
        public bool Enabled => true;
        public TimeSpan PollInterval => TimeSpan.FromSeconds(30);
        public int BatchSize => 10;
        public int MaxAttempts => 3;
        public TimeSpan IdleRelease => TimeSpan.FromMinutes(30);
        public int StaleAfterDays => 180;
        public TimeSpan ErrorCooldown => TimeSpan.FromHours(6);
        public TimeSpan ClaimPromotionAge => TimeSpan.FromMinutes(10);

        public IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType) =>
            entityType switch
            {
                VerificationEntityType.Airport => ["ourairports"],
                VerificationEntityType.Airline => ["aeroapi"],
                _ => ["mledoze"]
            };
    }

    private readonly Options _options = new();

    /// <summary>Captured by the <see cref="IAirportRepository"/> factory registration in
    /// <see cref="CreateProvisioner"/> the moment it's resolved from the provisioner's own scope --
    /// proves that scope actually carries the <c>system</c> actor, rather than trusting the
    /// unrelated <c>Stamp()</c>/<c>Verify(...)</c> assertion to stand in for that.</summary>
    private string? _actorOnScope;

    /// <summary>A stand-in source whose answers the test dictates, with a cost so the metered
    /// gate can be exercised.</summary>
    private sealed class FakeSource(
        string name,
        decimal cost,
        VerificationEntityType[] supports,
        Func<VerificationSourceLookupKey, VerificationSourceFetchResult> answer) : IVerificationSource
    {
        public int Calls { get; private set; }
        public string Name => name;
        public decimal CostPerRowUsd => cost;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes => supports;

        public Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer(key));
        }

        public void ReleaseCache(TimeSpan idleRelease) { }
        public bool HasCachedData => false;
    }

    private static VerificationSourceFetchResult FoundAirport(string iata) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airport.Iata)] = iata,
            [nameof(Airport.Icao)] = "ZZZZ",
            [nameof(Airport.Name)] = "Test Airport",
            [nameof(Airport.City)] = "Testville",
            [nameof(Airport.CountryCode)] = "GB",
            [nameof(Airport.Latitude)] = "51.5",
            [nameof(Airport.Longitude)] = "-0.45",
            [nameof(Airport.ElevationFt)] = "83"
        });

    /// <summary>Builds the provisioner over a real ServiceProvider, because it resolves its
    /// repositories from a scope of its own rather than taking them as constructor dependencies.</summary>
    private ReferenceDataProvisioner CreateProvisioner()
    {
        var services = new ServiceCollection();
        services.AddScoped<AdminContext>();
        services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
        services.AddScoped<IAirportRepository>(sp =>
        {
            // Reads the actor off the very scope the provisioner resolves this repository from --
            // the scope it must be VerificationJobActor.CreateScope, not the caller's own scope.
            _actorOnScope = sp.GetRequiredService<IAdminContext>().ActorId;
            return _airports;
        });
        services.AddScoped(_ => _airlines);
        services.AddScoped(_ => _countries);
        var provider = services.BuildServiceProvider();

        return new ReferenceDataProvisioner(
            _registry,
            new JobOptions(),
            _countryNames,
            _cache,
            _budget,
            _options,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReferenceDataProvisioner>.Instance);
    }

    public ReferenceDataProvisionerTests()
    {
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        _countries.FindByCodeAsync("GB", Arg.Any<CancellationToken>())
            .Returns(Country.Create("GB", "United Kingdom"));
    }

    [Fact]
    public async Task UnknownAirport_ThatTheSourceHas_IsCreatedAndVerified()
    {
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airports.Received(1).AddAsync(
            Arg.Is<Airport>(a =>
                a.Iata == "LHR" &&
                a.Name == "Test Airport" &&
                a.City == "Testville" &&
                a.CountryCode == "GB" &&
                a.IsVerified &&
                a.VerifiedBy == VerificationJobActor.ActorId &&
                a.VerificationSource == "ourairports" &&
                a.SourceType == "auto"),
            Arg.Any<CancellationToken>());
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(VerificationJobActor.ActorId, _actorOnScope);
    }

    [Fact]
    public async Task UnknownAirport_ThatTheSourceDoesNotHave_IsNotFound()
    {
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
            _ => VerificationSourceFetchResult.NotFound);
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airport, result.FailedType);
        Assert.Equal("ZZZ", result.FailedCode);
        await _airports.DidNotReceive().AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceError_IsUnavailable_NotNotFound()
    {
        // The distinction is the whole point: a failing source must never be reported to a traveler
        // as "this airport does not exist".
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
            _ => VerificationSourceFetchResult.SourceError("ourairports download failed: HTTP 503"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal("ourairports download failed: HTTP 503", result.DiagnosticDetail);
    }

    [Fact]
    public async Task Disabled_ReportsNotFoundWithoutTouchingASource()
    {
        _options.Enabled = false;
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task UnregisteredSource_IsUnavailable_NotNotFound()
    {
        // A misconfiguration is not evidence the entity is absent.
        _registry.TryGet("ourairports").Returns((IVerificationSource?)null);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task NoConfiguredSourceSupportsTheType_IsUnavailable_NotNotFound()
    {
        // Reachable by configuration alone: Verification:DefaultSources:Airport could name a
        // source (here standing in for e.g. mledoze) that is registered and answers fine, but
        // simply doesn't support airports at all. Nothing ever actually answered the airport
        // question, so this must not be reported as "this airport does not exist".
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Country],
            _ => VerificationSourceFetchResult.NotFound);
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        await _airports.DidNotReceive().AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirport_WithNoCountryCode_IsNotFound_NotAnException()
    {
        // An airport with no country cannot satisfy its `countries` foreign key, so it is
        // genuinely unprovisionable -- the same situation as an airline whose country name won't
        // resolve. It must never reach Airport.Create, which would NullReferenceException on
        // ToCanonicalCode().
        var noCountry = VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airport.Iata)] = "LHR",
            [nameof(Airport.Icao)] = "ZZZZ",
            [nameof(Airport.Name)] = "Test Airport",
            [nameof(Airport.City)] = "Testville",
            [nameof(Airport.CountryCode)] = null,
            [nameof(Airport.Latitude)] = "51.5",
            [nameof(Airport.Longitude)] = "-0.45",
            [nameof(Airport.ElevationFt)] = "83"
        });
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => noCountry);
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airport, result.FailedType);
        Assert.Equal("LHR", result.FailedCode);
        await _airports.DidNotReceive().AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingMissing_IsProvisionedAndWritesNothing()
    {
        var result = await CreateProvisioner().ProvisionAsync([], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
