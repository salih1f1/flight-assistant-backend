using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Configuration.Entities;

namespace FlightAssistant.Domain.Tests.Configuration
{
    public class AeroApiCallTests
    {
        private static readonly DateTime CalledUtc = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Record_WithValidValues_SetsAllProperties()
        {
            var call = AeroApiCall.Record(
                "/flights/{ident}", "get", 200, 123, AeroApiCallOutcome.Success, "AeroApiFlightStatusProvider", CalledUtc);

            Assert.Equal("/flights/{ident}", call.EndpointTemplate);
            Assert.Equal("GET", call.Method);
            Assert.Equal(200, call.StatusCode);
            Assert.Equal(123, call.DurationMs);
            Assert.Equal(AeroApiCallOutcome.Success, call.Outcome);
            Assert.Equal("AeroApiFlightStatusProvider", call.Caller);
            Assert.Equal(CalledUtc, call.CalledUtc);
        }

        [Fact]
        public void Record_NormalizesMethodToUppercase()
        {
            var call = AeroApiCall.Record(
                "/flights/{ident}", "get", 200, 0, AeroApiCallOutcome.Success, "caller", CalledUtc);

            Assert.Equal("GET", call.Method);
        }

        [Fact]
        public void Record_TrimsStringFields()
        {
            var call = AeroApiCall.Record(
                "  /flights/{ident}  ", "  get  ", 200, 0, AeroApiCallOutcome.Success, "  caller  ", CalledUtc);

            Assert.Equal("/flights/{ident}", call.EndpointTemplate);
            Assert.Equal("GET", call.Method);
            Assert.Equal("caller", call.Caller);
        }

        [Fact]
        public void Record_WithNullStatusCode_Accepts()
        {
            var call = AeroApiCall.Record(
                "/flights/{ident}", "GET", null, 0, AeroApiCallOutcome.Failed, "caller", CalledUtc);

            Assert.Null(call.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Record_WithEmptyEndpointTemplate_ThrowsDomainException(string? endpointTemplate)
        {
            Assert.Throws<DomainException>(() =>
                AeroApiCall.Record(endpointTemplate!, "GET", 200, 0, AeroApiCallOutcome.Success, "caller", CalledUtc));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Record_WithEmptyMethod_ThrowsDomainException(string? method)
        {
            Assert.Throws<DomainException>(() =>
                AeroApiCall.Record("/flights/{ident}", method!, 200, 0, AeroApiCallOutcome.Success, "caller", CalledUtc));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Record_WithEmptyCaller_ThrowsDomainException(string? caller)
        {
            Assert.Throws<DomainException>(() =>
                AeroApiCall.Record("/flights/{ident}", "GET", 200, 0, AeroApiCallOutcome.Success, caller!, CalledUtc));
        }

        [Fact]
        public void Record_WithNegativeDuration_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() =>
                AeroApiCall.Record("/flights/{ident}", "GET", 200, -1, AeroApiCallOutcome.Success, "caller", CalledUtc));
        }
    }
}
