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

/// <summary>
/// A minimal <see cref="IVerificationQueue"/> double mirroring the one part of
/// <c>VerificationRequestRepository.EnqueueAsync</c>'s behaviour these admin-handler tests care
/// about: its #418 error-cooldown gate applies to <see cref="VerificationTrigger.Flight"/> only.
/// The real gate is already pinned against Postgres by
/// <c>VerificationRequestRepositoryPostgresTests.EnqueueAsync_AdminTrigger_QueuesDespiteAnErrorInsideTheWindow_AndIssuesNoExtraQuery</c>;
/// this double exists so <see cref="RetryVerificationRequestCommandHandlerTests"/> and
/// <see cref="QueueVerificationRequestsCommandTests"/> can pin the same guarantee one layer up, at
/// the handler that turns a queue's <c>Skipped &gt; 0</c> into a 409 -- a plain NSubstitute mock
/// answers whatever a test tells it to and so can't tell a correctly-scoped cooldown from one that
/// has quietly started gating admin triggers too.
/// </summary>
internal sealed class CooldownGatedFakeQueue : IVerificationQueue
{
    private readonly HashSet<string> _cooling;

    /// <param name="coolingKeys">Keys whose most recent completed check is, in this fixture, an
    /// Error still inside the cooldown window.</param>
    public CooldownGatedFakeQueue(params string[] coolingKeys) =>
        _cooling = coolingKeys.ToHashSet(StringComparer.Ordinal);

    public List<VerificationTrigger> Calls { get; } = new();

    public Task<VerificationEnqueueResult> EnqueueAsync(
        VerificationEntityType entityType,
        IReadOnlyCollection<string> keys,
        IReadOnlyCollection<string> sources,
        VerificationTrigger trigger,
        string requestedBy,
        decimal estimatedCostUsd,
        CancellationToken ct = default)
    {
        Calls.Add(trigger);

        // Mirrors VerificationRequestRepository.EnqueueAsync's own
        // "trigger == VerificationTrigger.Flight" gate (#418, R2). Comment out that clause here
        // (leaving only the cooling-key check) to reproduce the regression this fixture exists to
        // catch: an admin trigger would then also get skipped, and the handler tests above would
        // see a 409 instead of a queued result.
        var skipped = trigger == VerificationTrigger.Flight
            ? keys.Count(_cooling.Contains)
            : 0;

        return Task.FromResult(new VerificationEnqueueResult(keys.Count - skipped, skipped));
    }
}
