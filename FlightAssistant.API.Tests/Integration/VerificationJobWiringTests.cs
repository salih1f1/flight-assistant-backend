using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The real host's registrations for the verification job (#367). The integration factory strips
/// hosted services, so this resolves what the background service resolves each cycle, from the host
/// <c>Program.cs</c> builds: a missing registration fails here, not at the first cycle in production.
/// </summary>
[Collection("Integration")]
public class VerificationJobWiringTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public void TheHost_ResolvesTheCycleRunnerAndProcessor_OverTheBoundOptions()
    {
        using var scope = factory.Services.CreateScope();

        Assert.IsType<VerificationCycleRunner>(scope.ServiceProvider.GetRequiredService<IVerificationCycleRunner>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VerificationRequestProcessor>());
        Assert.Same(
            factory.Services.GetRequiredService<IOptions<VerificationOptions>>().Value,
            scope.ServiceProvider.GetRequiredService<IVerificationJobOptions>());
        Assert.Same(
            factory.Services.GetRequiredService<VerificationJobHeartbeat>(),
            scope.ServiceProvider.GetRequiredService<VerificationJobHeartbeat>());
    }
}
