using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// The two guarantees no other suite can see (#465, Task 12). <see cref="ReferenceDataProvisionerTests"/>
/// (Application) runs entirely against NSubstitute repository doubles, so it cannot prove that the
/// countries this service writes actually land before the airports/airlines that reference them --
/// the InMemory provider the API suite runs on enforces no foreign key at all, so a country
/// inserted *after* its airport would pass there and fail only in production. Separately,
/// <c>FlightAssistantWebApplicationFactory</c> strips <see cref="AdminAuditInterceptor"/> entirely
/// (#319), so no API-level test can prove an audit row for a provisioned row was ever written.
///
/// <para>
/// Follows <see cref="AdminAuditInterceptorPostgresTests"/>'s fixture/collection pattern rather
/// than inventing a new harness: a real <see cref="FlightAssistantDbContext"/> against the shared
/// Testcontainers PostgreSQL instance, wired with the real <see cref="AdminAuditInterceptor"/>.
/// Unlike that file, the provisioner needs its repositories resolved through its own DI scope (see
/// <see cref="ReferenceDataProvisioner.ProvisionCoreAsync"/>'s <c>VerificationJobActor.CreateScope</c>
/// call), so the wiring here goes through a small <see cref="ServiceCollection"/> rather than a bare
/// <c>new FlightAssistantDbContext(...)</c> -- the scoped <see cref="AdminContext"/> the provisioner's
/// own scope mutates with the <c>system</c> actor has to be the exact instance
/// <see cref="AdminAuditInterceptor"/> reads from, which only holds if both are resolved from the
/// same DI scope.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReferenceProvisioningPostgresTests : PostgresTestBase
{
    private readonly PostgresContainerFixture _fixture;
    private readonly IVerificationSourceRegistry _registry = Substitute.For<IVerificationSourceRegistry>();
    private readonly ICountryNameResolver _countryNames = Substitute.For<ICountryNameResolver>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();

    public ReferenceProvisioningPostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        // Same reasoning as ReferenceDataProvisionerTests: NSubstitute's default for an
        // unconfigured Task<string?> is "", not null, which would read as an existing negative-
        // cache hit and short-circuit every test below before it ever reaches a source.
        _cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
    }

    private sealed class Options : IReferenceProvisioningOptions
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromHours(24);
    }

    // Read IVerificationJobOptions and implement every member it declares, mirroring
    // ReferenceDataProvisionerTests's own JobOptions fake -- only DefaultSourcesFor is exercised.
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

    /// <summary>A stand-in source whose answer is fixed at construction, mirroring
    /// ReferenceDataProvisionerTests's FakeSource but trimmed to what this file needs.</summary>
    private sealed class FakeSource(
        string name,
        VerificationEntityType[] supports,
        Func<VerificationSourceLookupKey, VerificationSourceFetchResult> answer) : IVerificationSource
    {
        public string Name => name;
        public decimal CostPerRowUsd => 0m;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes => supports;

        public Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default) =>
            Task.FromResult(answer(key));

        public void ReleaseCache(TimeSpan idleRelease) { }
        public bool HasCachedData => false;
    }

    private static VerificationSourceFetchResult FoundAirport(string iata) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airport.Iata)] = iata,
            [nameof(Airport.Icao)] = "EGLL",
            [nameof(Airport.Name)] = "London Heathrow",
            [nameof(Airport.City)] = "London",
            [nameof(Airport.CountryCode)] = "GB",
            [nameof(Airport.Latitude)] = "51.4706",
            [nameof(Airport.Longitude)] = "-0.461941",
            [nameof(Airport.ElevationFt)] = "83"
        });

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

    /// <summary>
    /// Builds a <see cref="ReferenceDataProvisioner"/> over real repositories bound to a single
    /// scoped <see cref="FlightAssistantDbContext"/> carrying the real <see cref="AdminAuditInterceptor"/>
    /// -- the same context shape <c>AdminAuditInterceptorPostgresTests.CreateAuditedDbContext</c>
    /// builds by hand, but resolved through DI here so that <c>VerificationJobActor.CreateScope</c>
    /// (invoked *inside* <see cref="ReferenceDataProvisioner.ProvisionAsync"/>, not by this method)
    /// mutates the exact <see cref="AdminContext"/> instance the interceptor reads the actor from:
    /// both are resolved from the one DI scope the provisioner creates for itself.
    /// </summary>
    private ReferenceDataProvisioner CreateProvisioner(Options options)
    {
        var services = new ServiceCollection();
        services.AddScoped<AdminContext>();
        services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
        services.AddScoped(sp =>
        {
            var adminContext = sp.GetRequiredService<IAdminContext>();
            var dbOptions = new DbContextOptionsBuilder<FlightAssistantDbContext>()
                .UseNpgsql(_fixture.ConnectionString)
                .AddInterceptors(new AdminAuditInterceptor(adminContext))
                .Options;
            return new FlightAssistantDbContext(dbOptions);
        });
        services.AddScoped<IAirportRepository>(sp =>
            new AirportRepository(sp.GetRequiredService<FlightAssistantDbContext>()));
        services.AddScoped<IAirlineRepository>(sp =>
            new AirlineRepository(sp.GetRequiredService<FlightAssistantDbContext>()));
        services.AddScoped<ICountryRepository>(sp =>
            new CountryRepository(sp.GetRequiredService<FlightAssistantDbContext>()));
        var provider = services.BuildServiceProvider();

        return new ReferenceDataProvisioner(
            _registry,
            new JobOptions(),
            _countryNames,
            _cache,
            _budget,
            options,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReferenceDataProvisioner>.Instance);
    }

    [Fact]
    public async Task ProvisioningAnAirportInAnUnknownCountry_SatisfiesTheForeignKey()
    {
        // The country row must be INSERTed before the airport that references it. InMemory enforces
        // no foreign key, so only a real PostgreSQL run can prove this ordering holds.
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));
        var provisioner = CreateProvisioner(new Options());

        var result = await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);

        await using var db = _fixture.CreateDbContext();
        Assert.NotNull(await db.Countries.SingleOrDefaultAsync(c => c.Code == "GB"));
        var airport = await db.Airports.SingleAsync(a => a.Iata == "LHR");
        Assert.Equal("GB", airport.CountryCode);
        Assert.True(airport.IsVerified);
        Assert.Equal("auto", airport.SourceType);
    }

    [Fact]
    public async Task ProvisionedRows_AreAuditedUnderTheSystemActor()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));
        var provisioner = CreateProvisioner(new Options());

        await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await using var db = _fixture.CreateDbContext();
        var rows = await db.AdminAuditLogs
            .Where(a => a.EntityType == nameof(Airport) || a.EntityType == nameof(Country))
            .ToListAsync();

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(VerificationJobActor.ActorId, r.ActorId));
    }

    [Fact]
    public async Task AFailedProvision_LeavesNoPartialRows()
    {
        // The airport resolves but its country does not, so nothing may be committed: an airport
        // row without its country would violate the foreign key anyway.
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", [VerificationEntityType.Country], _ => VerificationSourceFetchResult.NotFound));
        var provisioner = CreateProvisioner(new Options());

        var result = await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);

        await using var db = _fixture.CreateDbContext();
        Assert.Empty(await db.Airports.Where(a => a.Iata == "LHR").ToListAsync());
        Assert.Empty(await db.Countries.Where(c => c.Code == "GB").ToListAsync());
    }

    private static VerificationSourceFetchResult FoundAirline(string iata, string icao) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airline.Iata)] = iata,
            [nameof(Airline.Icao)] = icao,
            [nameof(Airline.Name)] = "Pegasus",
            [VerificationExtraValues.AirlineCountryName] = "Turkey"
        });

    [Fact]
    public async Task AnAirlineWeHoldByIcaoOnly_IsCompleted_RatherThanViolatingItsUniqueIndex()
    {
        // The case the feature bites hardest on: airlines.Icao carries a filtered unique index
        // (AirlineConfiguration), Airline.Iata is nullable, ImportAirlinesCommand explicitly
        // permits an ICAO-only row, and GetAirlineIdByIataAsync matches on IATA alone -- so
        // tracking PC2145 misses, and inserting aeroapi's {PC, PGT} row violates that index. Only
        // a real PostgreSQL run can see this: the InMemory provider enforces no unique index at
        // all, so the pre-fix insert saved happily there while producing a permanent, re-billed
        // 503 in production.
        await using (var seed = _fixture.CreateDbContext())
        {
            seed.Countries.Add(Country.Create("TR", "Turkey"));
            seed.Airlines.Add(Airline.Create(null, "PGT", "Pegasus Airlines", "TR"));
            await seed.SaveChangesAsync();
        }

        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", [VerificationEntityType.Airline], _ => FoundAirline("PC", "PGT")));
        // The completion path never resolves a country name -- the held row's own CountryCode
        // already satisfies the foreign key. This is stubbed anyway so that the *pre-fix* code
        // reaches its INSERT and fails on the unique index, which is the behaviour under test:
        // without it, reverting the fix would fail this test on an unconfigured-double
        // NullReferenceException at the resolver instead, and the index would never be exercised.
        _countryNames.TryResolveCodeByNameAsync("Turkey", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("TR"));
        var provisioner = CreateProvisioner(new Options());

        var result = await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "PC")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);

        await using var db = _fixture.CreateDbContext();
        var airline = Assert.Single(await db.Airlines.Where(a => a.Icao == "PGT").ToListAsync());
        Assert.Equal("PC", airline.Iata);                   // the code the row lacked
        Assert.Equal("Pegasus Airlines", airline.Name);     // a value it already had, kept
        Assert.Equal("TR", airline.CountryCode);
        // And the stub above went unused: completing a held row resolves no country name.
        await _countryNames.DidNotReceive()
            .TryResolveCodeByNameAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAirportWeHoldByIcao_IsCompleted_RatherThanViolatingItsUniqueIndex()
    {
        // airports.Icao carries the same filtered unique index (AirportConfiguration), so the same
        // insert fails the same way -- again invisible to every non-PostgreSQL suite.
        await using (var seed = _fixture.CreateDbContext())
        {
            seed.Countries.Add(Country.Create("GB", "United Kingdom"));
            seed.Airports.Add(Airport.Create("ZZZ", "EGLL", name: "", city: "", countryCode: "GB"));
            await seed.SaveChangesAsync();
        }

        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        var provisioner = CreateProvisioner(new Options());

        var result = await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);

        await using var db = _fixture.CreateDbContext();
        var airport = Assert.Single(await db.Airports.Where(a => a.Icao == "EGLL").ToListAsync());
        Assert.Equal("London Heathrow", airport.Name);      // blank columns filled from the source
        Assert.Equal("London", airport.City);
        Assert.Equal("ZZZ", airport.Iata);                  // a code the row already has, kept
    }
}
