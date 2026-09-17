using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Infrastructure.Tests.TestSupport;

/// <summary>
/// A minimal <see cref="IVerificationJobOptions"/> for a repository test that isn't about the job
/// cycle itself -- <c>VerificationRequestRepository</c> has taken this dependency since #418 (the
/// error-cooldown skip), so any direct construction site needs one. <see cref="ErrorCooldown"/>
/// defaults to zero (disabled), matching the sibling fake in Application.Tests, so a test that isn't
/// about the cooldown sees exactly the behaviour <c>EnqueueAsync</c> always had.
/// </summary>
internal sealed record FakeJobOptions(
    int BatchSize = 10, int MaxAttempts = 3, TimeSpan IdleRelease = default, int StaleAfterDays = 180,
    TimeSpan ErrorCooldown = default)
    : IVerificationJobOptions
{
    /// <summary>Not a positional parameter: a <see cref="TimeSpan"/> default must be constant, and
    /// <c>TimeSpan.FromMinutes(10)</c> isn't one. Matches
    /// <see cref="FlightAssistant.API.HostedServices.VerificationOptions"/>'s own default (#419), so a
    /// test that isn't about promotion sees claim ordering unaffected at realistic ages. A test that
    /// needs a different age sets it with an object initializer.</summary>
    public TimeSpan ClaimPromotionAge { get; init; } = TimeSpan.FromMinutes(10);

    public IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType) => entityType switch
    {
        VerificationEntityType.Airport => new[] { "ourairports" },
        VerificationEntityType.Airline => new[] { "aeroapi" },
        VerificationEntityType.Country => new[] { "mledoze" },
        _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.")
    };
}
