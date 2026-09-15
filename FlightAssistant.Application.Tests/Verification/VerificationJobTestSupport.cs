using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>Job settings for a test.</summary>
internal sealed record FakeJobOptions(int BatchSize = 10, int MaxAttempts = 3) : IVerificationJobOptions;

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

    public void ReleaseCache()
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
    public List<IReadOnlyCollection<string>> ReleaseCalls { get; } = new();

    public IVerificationSource? TryGet(string name) => _byName.GetValueOrDefault(name);

    public void ReleaseUnused(IReadOnlyCollection<string> sourceNamesInUse)
    {
        ReleaseCalls.Add(sourceNamesInUse.ToList());
        foreach (var source in Sources.Where(s => !sourceNamesInUse.Contains(s.Name)))
            source.ReleaseCache();
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
