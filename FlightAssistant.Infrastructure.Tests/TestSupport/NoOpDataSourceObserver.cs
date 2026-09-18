using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;

namespace FlightAssistant.Infrastructure.Tests.TestSupport;

/// <summary>
/// An observer that records nothing, for tests that construct an observed collaborator (such as
/// <c>RedisCacheService</c>) but are not about the observation — see
/// <c>NonHttpDataSourceSeamTests</c> for the tests that are.
/// </summary>
public sealed class NoOpDataSourceObserver : IDataSourceObserver
{
    public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null) { }
}
