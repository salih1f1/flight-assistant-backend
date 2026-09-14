using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Export;

public class ExportAirportsQueryHandlerTests
{
    private readonly IVerifiableRepository<Airport> _repo = Substitute.For<IVerifiableRepository<Airport>>();

    [Fact]
    public async Task Handle_ReturnsVerifiedAirportsMappedToExportDto()
    {
        var airport = Airport.Create("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83);
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        airport.Verify("admin@example.com", "manual", verifiedAt);
        _repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>()).Returns(new List<Airport> { airport });
        var handler = new ExportAirportsQueryHandler(_repo);

        var result = await handler.Handle(new ExportAirportsQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("LHR", result[0].Iata);
        Assert.Equal("EGLL", result[0].Icao);
        Assert.Equal("Heathrow", result[0].Name);
        Assert.Equal("London", result[0].City);
        Assert.Equal("GB", result[0].CountryCode);
        Assert.Equal(51.477, result[0].Latitude);
        Assert.Equal(-0.461, result[0].Longitude);
        Assert.Equal(83, result[0].ElevationFt);
        Assert.Equal(verifiedAt, result[0].VerifiedAt);
        Assert.Equal("admin@example.com", result[0].VerifiedBy);
        Assert.Equal("manual", result[0].VerificationSource);
    }

    [Fact]
    public async Task Handle_NoVerifiedAirports_ReturnsEmptyList()
    {
        _repo.GetAllVerifiedAsync(Arg.Any<CancellationToken>()).Returns(new List<Airport>());
        var handler = new ExportAirportsQueryHandler(_repo);

        var result = await handler.Handle(new ExportAirportsQuery(), CancellationToken.None);

        Assert.Empty(result);
    }
}
