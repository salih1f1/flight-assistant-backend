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
using NSubstitute.ExceptionExtensions;

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

        // NSubstitute's built-in default for an unconfigured member returning `string` is "",
        // never null -- and that default value provider applies through Task<string> too. A real
        // ICacheService returns null for a genuine cache miss (see e.g. SyncFlightStatusCommand's
        // TravelerAttemptMarkedAsync), so without this every unconfigured GetAsync<string> call in
        // this file would read as an existing negative-cache hit ("" is not null) and every test
        // below that doesn't care about caching would short-circuit before ever reaching a source.
        // A test that wants an actual cached hit configures its own, more specific `Returns` on top
        // of this, which NSubstitute matches first.
        _cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        // Every airport FoundAirport() below returns is in "GB", so this is the default "the
        // country is already on file" case for every test that doesn't care about country
        // provisioning -- EnsureCountriesAsync reads countries in a batch via FindByCodesAsync,
        // never the single-code FindByCodeAsync, which nothing in the provisioner calls. Tests
        // about an unknown or missing country override this themselves.
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
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

    private static VerificationSourceFetchResult FoundCountry(string code, string name) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Country.Code)] = code,
            [nameof(Country.Name)] = name,
            [nameof(Country.Alpha3)] = "GBR",
            [nameof(Country.NumericCode)] = "826",
            [nameof(Country.Region)] = "Europe",
            [nameof(Country.Subregion)] = "Northern Europe"
        });

    [Fact]
    public async Task AirportInAnUnknownCountry_ProvisionsTheCountryToo()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _countries.Received(1).AddAsync(
            Arg.Is<Country>(c =>
                c.Code == "GB" &&
                c.Name == "United Kingdom" &&
                c.Alpha3 == "GBR" &&
                c.NumericCode == 826 &&
                c.IsVerified &&
                c.VerificationSource == "mledoze"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AirportInAKnownCountry_DoesNotTouchTheCountrySource()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        var countrySource = new FakeSource("mledoze", 0m, [VerificationEntityType.Country],
            _ => FoundCountry("GB", "United Kingdom"));
        _registry.TryGet("mledoze").Returns(countrySource);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Equal(0, countrySource.Calls);
        await _countries.DidNotReceive().AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoAirportsInOneCountry_ResolveThatCountryOnce()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], key => FoundAirport(key.Iata!)));
        var countrySource = new FakeSource("mledoze", 0m, [VerificationEntityType.Country],
            _ => FoundCountry("GB", "United Kingdom"));
        _registry.TryGet("mledoze").Returns(countrySource);

        await CreateProvisioner().ProvisionAsync(
            [
                new MissingReference(VerificationEntityType.Airport, "LHR"),
                new MissingReference(VerificationEntityType.Airport, "LGW")
            ],
            CancellationToken.None);

        Assert.Equal(1, countrySource.Calls);
        await _countries.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AirportWhoseCountryTheSourceDoesNotHave_IsNotFoundOnTheCountry()
    {
        // The airport exists but we cannot satisfy its foreign key, so the failure is reported
        // against the country — that is the row that is genuinely absent everywhere.
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country],
                _ => VerificationSourceFetchResult.NotFound));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Country, result.FailedType);
        Assert.Equal("GB", result.FailedCode);
        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACountryAskedForDirectly_IsProvisioned()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Country, "GB")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _countries.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static VerificationSourceFetchResult FoundAirline(string iata, string? countryName) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airline.Iata)] = iata,
            [nameof(Airline.Icao)] = "BAW",
            [nameof(Airline.Name)] = "British Airways",
            [VerificationExtraValues.AirlineCountryName] = countryName
        });

    [Fact]
    public async Task UnknownAirline_ResolvesItsCountryNameToACode()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "United Kingdom")));
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("GB"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airlines.Received(1).AddAsync(
            Arg.Is<Airline>(a =>
                a.Iata == "BA" &&
                a.Icao == "BAW" &&
                a.Name == "British Airways" &&
                a.CountryCode == "GB" &&
                a.IsVerified &&
                a.VerificationSource == "aeroapi" &&
                a.SourceType == "auto"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_WhoseCountryNameCannotBeResolved_IsNotFound()
    {
        // No country code means no valid Airline row — the FK is required. That is a data gap, not
        // a transient failure, so it is a 404 rather than a 503.
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "Atlantis")));
        _countryNames.TryResolveCodeByNameAsync("Atlantis", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.NotFound);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airline, result.FailedType);
        Assert.Equal("BA", result.FailedCode);
        await _airlines.DidNotReceive().AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_WhenTheResolverFails_IsUnavailable()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "United Kingdom")));
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Failed("mledoze download failed: HTTP 503"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal("mledoze download failed: HTTP 503", result.DiagnosticDetail);
    }

    [Fact]
    public async Task UnknownAirline_WithNoCountryNameAtAll_IsNotFound()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", null)));
        _countryNames.TryResolveCodeByNameAsync(null, Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.NotFound);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airline, result.FailedType);
        // Pins that a blank name still goes through the resolver rather than being special-cased
        // before the call -- a regression that short-circuited on `countryName is null` would
        // still produce NotFound above, but would never make this call.
        await _countryNames.Received(1).TryResolveCodeByNameAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_InAnUnknownCountry_ProvisionsTheCountryToo()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "United Kingdom")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("GB"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _countries.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACachedMiss_SkipsTheSourceEntirely()
    {
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);
        _cache.GetAsync<string>(
                ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "ZZZ"),
                Arg.Any<CancellationToken>())
            .Returns("1");

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AFreshMiss_IsRemembered()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
                _ => VerificationSourceFetchResult.NotFound));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        await _cache.Received(1).SetAsync(
            ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "ZZZ"),
            Arg.Any<string>(),
            _options.NegativeCacheTtl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHit_IsNeverCached()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASourceError_IsNeverCached()
    {
        // Caching a failure would turn a brief outage into hours of 404s.
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
                _ => VerificationSourceFetchResult.SourceError("ourairports download failed: HTTP 503")));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroTtl_NeitherReadsNorWritesTheCache()
    {
        _options.NegativeCacheTtl = TimeSpan.Zero;
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
                _ => VerificationSourceFetchResult.NotFound));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        await _cache.DidNotReceive().GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCacheKey_IsCaseInsensitiveOnTheCode()
    {
        Assert.Equal(
            ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "zzz"),
            ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "ZZZ"));
    }

    [Theory]
    [InlineData(ProviderBudgetState.SoftLimit)]
    [InlineData(ProviderBudgetState.Exhausted)]
    public async Task AMeteredSource_IsHeldBackOnceTheBudgetLeavesNormal(ProviderBudgetState state)
    {
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);
        var source = new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline],
            _ => FoundAirline("BA", "United Kingdom"));
        _registry.TryGet("aeroapi").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AMeteredSource_RunsWhileTheBudgetIsNormal()
    {
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        var source = new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline],
            _ => FoundAirline("BA", "United Kingdom"));
        _registry.TryGet("aeroapi").Returns(source);
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("GB"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task AFreeSource_IsNeverGatedByTheBudget()
    {
        // ourairports and mledoze cost nothing; refusing them would withhold work that spends no money.
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Exhausted);
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task AFreeSource_NeverEvenReadsTheBudget()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _budget.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>A source that never answers within the test's budget.</summary>
    private sealed class StallingSource(string name, VerificationEntityType[] supports) : IVerificationSource
    {
        public string Name => name;
        public decimal CostPerRowUsd => 0m;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes => supports;

        public async Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return VerificationSourceFetchResult.NotFound;
        }

        public void ReleaseCache(TimeSpan idleRelease) { }
        public bool HasCachedData => false;
    }

    [Fact]
    public async Task ExceedingTheBudget_IsUnavailable_NotNotFound()
    {
        // A timeout must never be reported as "this airport does not exist": we simply ran out of
        // time. The shared download carries on, so the traveler's retry succeeds.
        _options.Budget = TimeSpan.FromMilliseconds(50);
        _registry.TryGet("ourairports").Returns(new StallingSource("ourairports", [VerificationEntityType.Airport]));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal(VerificationEntityType.Airport, result.FailedType);
        Assert.Equal("LHR", result.FailedCode);
    }

    [Fact]
    public async Task ExceedingTheBudget_WritesNothing()
    {
        _options.Budget = TimeSpan.FromMilliseconds(50);
        _registry.TryGet("ourairports").Returns(new StallingSource("ourairports", [VerificationEntityType.Airport]));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _countries.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCallersOwnCancellation_Propagates_RatherThanBecomingUnavailable()
    {
        // The traveler abandoned the request. That is not a provisioning outcome, and swallowing it
        // would have us go on assembling a response for a connection that has already gone (#453).
        _options.Budget = TimeSpan.FromSeconds(30);
        _registry.TryGet("ourairports").Returns(new StallingSource("ourairports", [VerificationEntityType.Airport]));
        using var caller = new CancellationTokenSource();
        caller.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvisioner().ProvisionAsync(
                [new MissingReference(VerificationEntityType.Airport, "LHR")], caller.Token));
    }

    [Fact]
    public async Task ACacheReadFailure_FailsOpen_ProvisioningStillSucceeds()
    {
        // FetchAsync's own cache-read catch (`when (ex is not OperationCanceledException)`, from
        // Task 7) is what lets an ordinary cache outage fail open instead of failing the whole
        // provision. The deadline above now depends on that filter excluding cancellation -- this
        // pins the other half: a non-cancellation failure must still fail open.
        _cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cache unavailable"));
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACacheReadThatThrowsCancellation_PropagatesRatherThanBeingSwallowed()
    {
        // Unlike the test above, this exception IS an OperationCanceledException -- the one kind
        // FetchAsync's cache-read catch must never treat as an ordinary outage. If that `when`
        // filter were ever removed, this would be caught, logged as a cache failure, and
        // provisioning would carry on past a cancellation it should have unwound for instead --
        // exactly how a real deadline firing mid-cache-read would end up silently absorbed rather
        // than reported. Distinct from TheCallersOwnCancellation_Propagates... above, which
        // exercises this same guarantee via a stalled *source* lookup rather than the cache: here
        // the caller's own token is already cancelled before the call even starts, so the outer
        // budget catch's `!ct.IsCancellationRequested` guard does not intercept it either, and the
        // exception must reach the caller unchanged rather than becoming a status.
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        _cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvisioner().ProvisionAsync(
                [new MissingReference(VerificationEntityType.Airport, "LHR")], caller.Token));
    }

    [Fact]
    public async Task ASaveFailure_IsUnavailable_NotProvisioned()
    {
        // Application cannot see EF Core's DbUpdateException (a deliberate layer boundary, #465
        // review), so any non-cancellation save failure is treated the same way. The concrete case
        // is the concurrent-duplicate race: Airport.Iata carries a unique index, so two
        // POST /flights requests provisioning the same new airport race -- one wins, and the
        // other's SaveChangesAsync throws here. A logged 503 whose retry succeeds immediately (the
        // row the other request just created is now there) beats an unhandled 500 on a
        // traveler-facing path. The diagnostic must be a fixed string, never the exception's own
        // message, which could carry a host, constraint or table name.
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _airports.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "23505: duplicate key value violates unique constraint \"ix_airports_iata\""));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal(VerificationEntityType.Airport, result.FailedType);
        Assert.Equal("LHR", result.FailedCode);
        Assert.Equal("Saving the provisioned reference data failed.", result.DiagnosticDetail);
    }

    [Fact]
    public async Task ASaveThatThrowsCancellation_PropagatesRatherThanBeingSwallowed()
    {
        // Same shape as ACacheReadThatThrowsCancellation... above: the save's own catch
        // (`when (ex is not OperationCanceledException)`) must never treat a genuine cancellation
        // as an ordinary save failure. The caller's own token is already cancelled here, so the
        // outer budget catch's `!ct.IsCancellationRequested` guard does not intercept it either,
        // and the exception must reach the caller unchanged rather than becoming a status.
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _airports.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvisioner().ProvisionAsync(
                [new MissingReference(VerificationEntityType.Airport, "LHR")], caller.Token));
    }
}
