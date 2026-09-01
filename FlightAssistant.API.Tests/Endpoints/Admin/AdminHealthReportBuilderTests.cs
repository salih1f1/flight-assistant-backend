using FlightAssistant.API.Endpoints.Admin;

namespace FlightAssistant.API.Tests.Endpoints.Admin;

/// <summary>
/// Direct unit tests of <see cref="AdminHealthReportBuilder.ParseVersion"/> — the only piece of the
/// builder's logic that needs no host, no <c>DbContext</c> and no <c>HealthCheckService</c> to
/// exercise. Everything else in <see cref="AdminHealthReportBuilder"/> is covered by
/// <c>AdminHealthEndpointTests</c> through the full <c>WebApplicationFactory</c> pipeline.
/// </summary>
public class AdminHealthReportBuilderTests
{
    [Fact]
    public void ParseVersion_WithSourceRevisionId_SplitsVersionAndShortensShaToSevenChars()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("1.0.0+abcdef1234");

        Assert.Equal("1.0.0", version);
        Assert.Equal("abcdef1", buildSha);
    }

    [Fact]
    public void ParseVersion_WithoutAPlus_ReturnsNullBuildSha()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("1.0.0");

        Assert.Equal("1.0.0", version);
        Assert.Null(buildSha);
    }

    [Fact]
    public void ParseVersion_ShaShorterThanSevenChars_IsReturnedAsIs()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("2.3.1+ab12");

        Assert.Equal("2.3.1", version);
        Assert.Equal("ab12", buildSha);
    }

    [Fact]
    public void ParseVersion_TrailingPlusWithNoRevisionId_ReturnsNullBuildSha()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("1.0.0+");

        Assert.Equal("1.0.0", version);
        Assert.Null(buildSha);
    }

    [Fact]
    public void ParseVersion_NullOrBlank_FallsBackToZeroVersionAndNullSha()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion(null);

        Assert.Equal("0.0.0", version);
        Assert.Null(buildSha);
    }
}
