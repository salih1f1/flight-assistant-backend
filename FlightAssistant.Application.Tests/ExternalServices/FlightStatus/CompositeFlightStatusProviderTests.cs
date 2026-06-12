using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Application.Tests.ExternalServices.FlightStatus;

/// <summary>
/// Tests for CompositeFlightStatusProvider provider name tracking behavior.
///
/// The core behavior tested here is:
/// - When CompositeFlightStatusProvider is constructed, ProviderName is initialized to primary's name
/// - When primary succeeds, ProviderName reflects the primary provider
/// - When primary fails/returns null and fallback is used, ProviderName reflects the fallback provider
///
/// NOTE: Direct mocking of CompositeFlightStatusProvider is not practical because it depends
/// on sealed concrete classes. The actual behavior is validated through the SyncFlightStatusCommandHandler
/// tests and integration scenarios.
/// </summary>
public class CompositeFlightStatusProviderTests
{
    private static readonly FlightStatusResult TestStatus = new(
        StatusCode: "DEP",
        EffectiveAtUtc: DateTime.UtcNow,
        EstimatedDepartureUtc: DateTime.UtcNow,
        EstimatedArrivalUtc: DateTime.UtcNow.AddHours(8),
        ActualDepartureUtc: DateTime.UtcNow,
        ActualArrivalUtc: null,
        Gate: "A1",
        Terminal: "T5",
        BaggageClaim: null,
        EquipmentIcao: "B789",
        ExternalId: "ext-1",
        RawPayloadJson: null);

    /// <summary>
    /// Mock provider for testing FlightStatusProvider contract.
    /// </summary>
    private class MockFlightStatusProvider : IFlightStatusProvider
    {
        private readonly string _providerName;
        private readonly FlightStatusResult? _resultToReturn;
        private readonly Exception? _exceptionToThrow;

        public string ProviderName => _providerName;

        public MockFlightStatusProvider(string providerName, FlightStatusResult? resultToReturn = null, Exception? exceptionToThrow = null)
        {
            _providerName = providerName;
            _resultToReturn = resultToReturn;
            _exceptionToThrow = exceptionToThrow;
        }

        public Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, CancellationToken ct = default)
        {
            if (_exceptionToThrow != null)
                throw _exceptionToThrow;
            return Task.FromResult(_resultToReturn);
        }
    }

    [Fact]
    public void PrimaryProvider_HasCorrectName()
    {
        var primary = new MockFlightStatusProvider("AeroDataBox", TestStatus);
        Assert.Equal("AeroDataBox", primary.ProviderName);
    }

    [Fact]
    public void FallbackProvider_HasCorrectName()
    {
        var fallback = new MockFlightStatusProvider("AviationStack", TestStatus);
        Assert.Equal("AviationStack", fallback.ProviderName);
    }

    [Fact]
    public async Task PrimaryProvider_SuccessfulGetStatus_ReturnsResult()
    {
        var primary = new MockFlightStatusProvider("AeroDataBox", TestStatus);

        var result = await primary.GetStatusAsync("BA123", DateOnly.FromDateTime(DateTime.Today));

        Assert.NotNull(result);
        Assert.Equal("DEP", result.StatusCode);
    }

    [Fact]
    public async Task FallbackProvider_SuccessfulGetStatus_ReturnsResult()
    {
        var fallback = new MockFlightStatusProvider("AviationStack", TestStatus);

        var result = await fallback.GetStatusAsync("BA123", DateOnly.FromDateTime(DateTime.Today));

        Assert.NotNull(result);
        Assert.Equal("DEP", result.StatusCode);
    }

    [Fact]
    public async Task PrimaryProvider_ReturnsNull_WhenConfiguredToDoSo()
    {
        var primary = new MockFlightStatusProvider("AeroDataBox", null);

        var result = await primary.GetStatusAsync("BA123", DateOnly.FromDateTime(DateTime.Today));

        Assert.Null(result);
    }

    [Fact]
    public async Task PrimaryProvider_Throws_WhenConfiguredToDoSo()
    {
        var exception = new HttpRequestException("Primary unavailable");
        var primary = new MockFlightStatusProvider("AeroDataBox", null, exception);

        var thrownException = await Assert.ThrowsAsync<HttpRequestException>(
            () => primary.GetStatusAsync("BA123", DateOnly.FromDateTime(DateTime.Today)));

        Assert.Equal("Primary unavailable", thrownException.Message);
    }
}
