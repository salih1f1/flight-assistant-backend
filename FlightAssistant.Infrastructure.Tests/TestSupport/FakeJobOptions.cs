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
    public IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType) => entityType switch
    {
        VerificationEntityType.Airport => new[] { "ourairports" },
        VerificationEntityType.Airline => new[] { "aeroapi" },
        VerificationEntityType.Country => new[] { "mledoze" },
        _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.")
    };
}
