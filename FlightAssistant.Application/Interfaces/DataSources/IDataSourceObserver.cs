using FlightAssistant.Application.DataSources;

namespace FlightAssistant.Application.Interfaces.DataSources
{
    /// <summary>
    /// Records what one call to a source saw. Implementations must not block the caller and must
    /// never throw: an observation is diagnostics, and a failure to record one must not fail the
    /// call it was describing.
    /// </summary>
    public interface IDataSourceObserver
    {
        void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null);
    }
}
