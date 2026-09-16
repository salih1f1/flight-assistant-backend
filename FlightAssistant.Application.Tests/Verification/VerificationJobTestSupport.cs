using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>Job settings for a test. <see cref="IdleRelease"/> defaults to zero: release at once.
/// <see cref="DefaultSourcesFor"/> mirrors <c>VerificationOptions</c>'s own defaults, since no test in
/// this file is about the flight-tracking trigger (#410/#418) that reads <see cref="DefaultSourcesFor"/>
/// or <see cref="ErrorCooldown"/>.</summary>
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

/// <summary>A budget guard whose <see cref="State"/> a test sets directly, defaulting to
/// <see cref="ProviderBudgetState.Normal"/> like the real guard's fail-open behaviour. Counts every
/// <see cref="GetStateAsync"/> call so a test can prove the processor/runner did -- or, once paused,
/// deliberately did not -- ask it again (R4/R5, #401 task 2).</summary>
internal sealed class FakeProviderBudgetGuard : IProviderBudgetGuard
{
    public ProviderBudgetState State { get; set; } = ProviderBudgetState.Normal;
    public int CallCount { get; private set; }

    public Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(State);
    }
}

/// <summary>A source whose answer the test decides, recording every lookup and release.</summary>
internal sealed class FakeVerificationSource : IVerificationSource
{
    private readonly Func<VerificationSourceLookupKey, CancellationToken, Task<VerificationSourceFetchResult>> _lookup;

    public FakeVerificationSource(
        string name,
        VerificationEntityType supported,
        Func<VerificationSourceLookupKey, CancellationToken, Task<VerificationSourceFetchResult>> lookup)
    {
        Name = name;
        SupportedEntityTypes = new[] { supported };
        _lookup = lookup;
    }

    public FakeVerificationSource(string name, VerificationEntityType supported, VerificationSourceFetchResult answer)
        : this(name, supported, (_, _) => Task.FromResult(answer))
    {
    }

    public string Name { get; }
    public decimal CostPerRowUsd => 0m;
    public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; }
    public List<VerificationSourceLookupKey> Lookups { get; } = new();
    public int ReleaseCount { get; private set; }

    /// <summary>Like a real source: set by a lookup, cleared by a release. A test can set it to stand
    /// for a dataset an earlier cycle loaded.</summary>
    public bool HasCachedData { get; set; }

    public Task<VerificationSourceFetchResult> LookupAsync(
        VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
    {
        Lookups.Add(key);
        HasCachedData = true;
        return _lookup(key, ct);
    }

    /// <summary>Releases whatever the idle period: the idle rule is Infrastructure's
    /// <c>VerificationDatasetCache</c>, covered by its own tests.</summary>
    public void ReleaseCache(TimeSpan idleRelease)
    {
        ReleaseCount++;
        HasCachedData = false;
    }
}

/// <summary>A registry over fixed sources, releasing like the real one and recording each call.</summary>
internal sealed class FakeVerificationSourceRegistry : IVerificationSourceRegistry
{
    private readonly Dictionary<string, IVerificationSource> _byName;

    public FakeVerificationSourceRegistry(params IVerificationSource[] sources)
    {
        _byName = sources.ToDictionary(s => s.Name, StringComparer.Ordinal);
        Sources = sources;
    }

    public IReadOnlyCollection<IVerificationSource> Sources { get; }
    public bool AnySourceHasCachedData => Sources.Any(s => s.HasCachedData);
    public List<(IReadOnlyCollection<string> SourceNamesInUse, TimeSpan IdleRelease)> ReleaseCalls { get; } = new();

    public IVerificationSource? TryGet(string name) => _byName.GetValueOrDefault(name);

    public void ReleaseUnused(IReadOnlyCollection<string> sourceNamesInUse, TimeSpan idleRelease)
    {
        ReleaseCalls.Add((sourceNamesInUse.ToList(), idleRelease));
        foreach (var source in Sources.Where(s => !sourceNamesInUse.Contains(s.Name)))
            source.ReleaseCache(idleRelease);
    }
}

internal static class ClaimedRequests
{
    /// <summary>
    /// A request as <c>ClaimBatchAsync</c> returns it: Running, with an id and the claim's
    /// <c>StartedUtc</c>. The claim is one SQL statement with no domain method, so those three are
    /// set by reflection; any earlier attempts go through the domain's own <c>RecordSourceError</c>.
    /// </summary>
    public static VerificationRequest Create(
        long id,
        VerificationEntityType entityType,
        string entityKey,
        IEnumerable<string> sources,
        DateTime startedUtc,
        int priorAttempts = 0)
    {
        var request = VerificationRequest.Create(
            entityType, entityKey, sources, VerificationTrigger.Admin, "admin-oid", 0m, startedUtc.AddHours(-1));

        for (var i = 0; i < priorAttempts; i++)
            request.RecordSourceError("earlier failure", startedUtc.AddMinutes(-30), maxAttempts: int.MaxValue, backoff: TimeSpan.Zero);

        Set(request, nameof(VerificationRequest.Id), id);
        Set(request, nameof(VerificationRequest.Status), VerificationRequestStatus.Running);
        Set(request, nameof(VerificationRequest.StartedUtc), (DateTime?)startedUtc);
        return request;
    }

    private static void Set(VerificationRequest request, string property, object? value) =>
        typeof(VerificationRequest).GetProperty(property)!.SetValue(request, value);
}
