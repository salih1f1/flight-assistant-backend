using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Verification
{
    public class VerificationSourceDefaultsTests
    {
        private readonly IProviderRouting _routing = Substitute.For<IProviderRouting>();
        private readonly IVerificationJobOptions _options = Substitute.For<IVerificationJobOptions>();

        private VerificationSourceDefaults Build() => new(_routing, _options);

        [Fact]
        public async Task ForAsync_UsesTheSwitchValue()
        {
            _routing.GetProvidersAsync(ProviderJobs.VerificationAirline, Arg.Any<CancellationToken>())
                    .Returns(new[] { "ourairports" });

            var sources = await Build().ForAsync(VerificationEntityType.Airline, default);

            Assert.Equal(new[] { "ourairports" }, sources);
        }

        [Fact]
        public async Task ForAsync_EmptySwitchValue_FallsBackToConfiguration()
        {
            // GetProvidersAsync already falls back to the configured default itself; this guards the
            // remaining case where a row exists but parses to nothing, so the caller never gets an
            // empty source list and silently queues a request that can never be answered.
            _routing.GetProvidersAsync(ProviderJobs.VerificationCountry, Arg.Any<CancellationToken>())
                    .Returns(Array.Empty<string>());
            _options.DefaultSourcesFor(VerificationEntityType.Country)
                    .Returns(new[] { "mledoze" });

            var sources = await Build().ForAsync(VerificationEntityType.Country, default);

            Assert.Equal(new[] { "mledoze" }, sources);
        }

        [Fact]
        public async Task ForAsync_PreservesAskOrder()
        {
            _routing.GetProvidersAsync(ProviderJobs.VerificationAirport, Arg.Any<CancellationToken>())
                    .Returns(new[] { "ourairports", "aeroapi" });

            var sources = await Build().ForAsync(VerificationEntityType.Airport, default);

            Assert.Equal(new[] { "ourairports", "aeroapi" }, sources);
        }
    }
}
