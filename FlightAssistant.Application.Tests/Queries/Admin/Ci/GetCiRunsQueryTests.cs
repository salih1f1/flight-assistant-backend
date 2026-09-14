using System.Text.Json;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Ci;
using FlightAssistant.Domain.Common.Exceptions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.Ci;

public class GetCiRunsQueryTests
{
    private static readonly CiRun Completed = new(
        Id: 34761036906,
        RunNumber: 32,
        RunAttempt: 1,
        Title: "Merge pull request #330 from salih1f1/fix/align-admin-panel-dev-origin",
        Event: "push",
        Branch: "development",
        CommitSha: "1cc8f1a2966782782a2456e85d91eaa2fe0ff192",
        Status: "completed",
        Conclusion: "success",
        CreatedUtc: new DateTimeOffset(2026, 9, 13, 13, 52, 9, TimeSpan.Zero),
        StartedUtc: new DateTimeOffset(2026, 9, 13, 13, 52, 9, TimeSpan.Zero),
        UpdatedUtc: new DateTimeOffset(2026, 9, 13, 13, 54, 29, TimeSpan.Zero),
        HtmlUrl: "https://github.com/salih1f1/flight-assistant-backend/actions/runs/34761036906");

    /// <summary>Dictionary-backed rather than a substitute, so a key that changed between calls would
    /// miss the way it would in production.</summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];
        public List<(string Key, TimeSpan? Ttl)> Writes { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            _entries[key] = value;
            Writes.Add((key, absoluteExpire));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private readonly ICiRunProvider _ci = Substitute.For<ICiRunProvider>();
    private readonly FakeCache _cache = new();
    private readonly GetCiRunsQueryHandler _handler;

    public GetCiRunsQueryTests()
    {
        _ci.GetRunsAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<CiRun> { Completed });
        _handler = new GetCiRunsQueryHandler(_ci, _cache);
    }

    // ── Caching ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ReturnsGitHubsRuns_AndCachesThemForAMinute()
    {
        var runs = await _handler.Handle(new GetCiRunsQuery(), CancellationToken.None);

        Assert.Equal(Completed, Assert.Single(runs));
        await _ci.Received(1).GetRunsAsync(null, 20, Arg.Any<CancellationToken>());
        var write = Assert.Single(_cache.Writes);
        Assert.Equal(TimeSpan.FromSeconds(60), write.Ttl);
    }

    [Fact]
    public async Task Handle_DoesNotAskGitHubAgain_OnACacheHit()
    {
        await _handler.Handle(new GetCiRunsQuery("development", 10), CancellationToken.None);
        var second = await _handler.Handle(new GetCiRunsQuery("development", 10), CancellationToken.None);

        Assert.Single(second);
        await _ci.Received(1).GetRunsAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_KeysTheCacheByBranchAndLimit()
    {
        await _handler.Handle(new GetCiRunsQuery(), CancellationToken.None);
        await _handler.Handle(new GetCiRunsQuery("development"), CancellationToken.None);
        await _handler.Handle(new GetCiRunsQuery(Limit: 5), CancellationToken.None);

        await _ci.Received(1).GetRunsAsync(null, 20, Arg.Any<CancellationToken>());
        await _ci.Received(1).GetRunsAsync("development", 20, Arg.Any<CancellationToken>());
        await _ci.Received(1).GetRunsAsync(null, 5, Arg.Any<CancellationToken>());
        Assert.Equal(3, _cache.Writes.Select(w => w.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_TreatsABlankBranchAsEveryBranch(string blank)
    {
        await _handler.Handle(new GetCiRunsQuery(blank), CancellationToken.None);
        await _handler.Handle(new GetCiRunsQuery(), CancellationToken.None);

        await _ci.Received(1).GetRunsAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _ci.Received(1).GetRunsAsync(null, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DoesNotCacheAFailure()
    {
        _ci.GetRunsAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("CI results are temporarily unavailable.", 502));

        await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(new GetCiRunsQuery(), CancellationToken.None));

        Assert.Empty(_cache.Writes);
    }

    [Fact]
    public void CachedRuns_SurviveAJsonRoundTrip_TheWayRedisCacheServiceStoresThem()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        IReadOnlyList<CiRun> runs = [Completed];

        var restored = JsonSerializer.Deserialize<IReadOnlyList<CiRun>>(JsonSerializer.Serialize(runs, options), options);

        Assert.Equal(Completed, Assert.Single(restored!));
        Assert.Equal(140, restored![0].DurationSeconds);
    }

    // ── Duration ─────────────────────────────────────────────────────────────

    [Fact]
    public void DurationSeconds_IsStartToLastUpdate_ForACompletedRun()
    {
        Assert.Equal(140, Completed.DurationSeconds);
    }

    [Fact]
    public void DurationSeconds_IsNull_UntilTheRunCompletes()
    {
        Assert.Null((Completed with { Status = "in_progress", Conclusion = null }).DurationSeconds);
        Assert.Null((Completed with { StartedUtc = null }).DurationSeconds);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void Validator_BoundsTheLimit(int limit, bool valid)
    {
        var result = new GetCiRunsQueryValidator().Validate(new GetCiRunsQuery(Limit: limit));

        Assert.Equal(valid, result.IsValid);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void Validator_BoundsTheBranchLength(int length, bool valid)
    {
        var result = new GetCiRunsQueryValidator().Validate(new GetCiRunsQuery(new string('a', length)));

        Assert.Equal(valid, result.IsValid);
    }
}
