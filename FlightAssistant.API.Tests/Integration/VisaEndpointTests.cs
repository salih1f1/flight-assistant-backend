using System.Net;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class VisaEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Theory]
    [InlineData("gb", "JP")]   // lowercase passport
    [InlineData("GB", "jp")]   // lowercase destination
    [InlineData("GBR", "JP")]  // alpha-3 passport
    [InlineData("", "JP")]     // empty passport
    public async Task GetVisa_InvalidCountryCode_Returns400(string passport, string destination)
    {
        var client = factory.CreateUserClient();
        var response = await client.GetAsync(
            $"visa?passportCountry={passport}&destinationCountry={destination}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetVisa_NoToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("visa?passportCountry=GB&destinationCountry=JP");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
