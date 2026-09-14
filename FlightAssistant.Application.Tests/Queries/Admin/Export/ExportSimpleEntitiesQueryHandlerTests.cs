using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Export;

public class ExportAirlinesQueryHandlerTests
{
    [Fact]
    public async Task Handle_MapsVerifiedAirlinesToExportDto()
    {
        var airline = Airline.Create("BA", "BAW", "British Airways", "GB", "OW");
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        airline.Verify("admin@example.com", "manual", verifiedAt);
        var repo = Substitute.For<IVerifiableRepository<Airline>>();
        repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Airline> { airline });
        var handler = new ExportAirlinesQueryHandler(repo);

        var result = await handler.Handle(new ExportAirlinesQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("BA", result[0].Iata);
        Assert.Equal("BAW", result[0].Icao);
        Assert.Equal("British Airways", result[0].Name);
        Assert.Equal("GB", result[0].CountryCode);
        Assert.Equal("OW", result[0].AllianceCode);
        Assert.Equal(verifiedAt, result[0].VerifiedAt);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }
}

public class ExportAlliancesQueryHandlerTests
{
    [Fact]
    public async Task Handle_MapsVerifiedAlliancesToExportDto()
    {
        var alliance = Alliance.Create("OW", "Oneworld");
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        alliance.Verify("admin@example.com", "manual", verifiedAt);
        var repo = Substitute.For<IVerifiableRepository<Alliance>>();
        repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Alliance> { alliance });
        var handler = new ExportAlliancesQueryHandler(repo);

        var result = await handler.Handle(new ExportAlliancesQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("OW", result[0].Code);
        Assert.Equal("Oneworld", result[0].Name);
        Assert.Equal(verifiedAt, result[0].VerifiedAt);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }
}

public class ExportCountriesQueryHandlerTests
{
    [Fact]
    public async Task Handle_MapsVerifiedCountriesToExportDto()
    {
        var country = Country.Create("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe", false, false, false);
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        country.Verify("admin@example.com", "manual", verifiedAt);
        var repo = Substitute.For<IVerifiableRepository<Country>>();
        repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Country> { country });
        var handler = new ExportCountriesQueryHandler(repo);

        var result = await handler.Handle(new ExportCountriesQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("GB", result[0].Code);
        Assert.Equal("United Kingdom", result[0].Name);
        Assert.Equal("GBR", result[0].Alpha3);
        Assert.Equal(826, result[0].NumericCode);
        Assert.False(result[0].IsEU);
        Assert.Equal(verifiedAt, result[0].VerifiedAt);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }
}

public class ExportLoungeProvidersQueryHandlerTests
{
    [Fact]
    public async Task Handle_MapsVerifiedLoungeProvidersToExportDto()
    {
        var provider = LoungeProvider.Create("PP", "Priority Pass", LoungeProviderKind.Contract, "Global lounge network");
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        provider.Verify("admin@example.com", "manual", verifiedAt);
        var repo = Substitute.For<IVerifiableRepository<LoungeProvider>>();
        repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LoungeProvider> { provider });
        var handler = new ExportLoungeProvidersQueryHandler(repo);

        var result = await handler.Handle(new ExportLoungeProvidersQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("PP", result[0].Code);
        Assert.Equal("Priority Pass", result[0].Name);
        Assert.Equal("Contract", result[0].Kind);
        Assert.Equal("Global lounge network", result[0].Description);
        Assert.Equal(verifiedAt, result[0].VerifiedAt);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }
}

public class ExportLoyaltyProgramKindsQueryHandlerTests
{
    [Fact]
    public async Task Handle_MapsVerifiedLoyaltyProgramKindsToExportDto()
    {
        var kind = LoyaltyProgramKind.Create("Airline", "Airline Programs", "Frequent flyer programs");
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        kind.Verify("admin@example.com", "manual", verifiedAt);
        var repo = Substitute.For<IVerifiableRepository<LoyaltyProgramKind>>();
        repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LoyaltyProgramKind> { kind });
        var handler = new ExportLoyaltyProgramKindsQueryHandler(repo);

        var result = await handler.Handle(new ExportLoyaltyProgramKindsQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Airline", result[0].Code);
        Assert.Equal("Airline Programs", result[0].Name);
        Assert.Equal("Frequent flyer programs", result[0].Description);
        Assert.Equal(verifiedAt, result[0].VerifiedAt);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }
}
