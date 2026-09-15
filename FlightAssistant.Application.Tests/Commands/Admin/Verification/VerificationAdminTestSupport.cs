using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Commands.Admin.Verification;

/// <summary>Sources with a name, a cost and supported types, for the admin verification tests.</summary>
internal static class TestSources
{
    public static IVerificationSource Source(string name, decimal costPerRowUsd, params VerificationEntityType[] supported)
    {
        var source = Substitute.For<IVerificationSource>();
        source.Name.Returns(name);
        source.CostPerRowUsd.Returns(costPerRowUsd);
        source.SupportedEntityTypes.Returns(supported);
        return source;
    }
}
