using FlightAssistant.Application.DataSources;

namespace FlightAssistant.Application.Interfaces.DataSources
{
    /// <summary>
    /// Checks one source on demand, because an operator pressed "Check now". The only thing in this
    /// feature that starts traffic of its own — everything else observes calls the application was
    /// making anyway.
    ///
    /// <para>
    /// An implementation returns the outcome and makes sure exactly one observation is recorded for
    /// it: a probe that goes through a named <see cref="HttpClient"/> is already recorded by
    /// <c>DataSourceObservingHandler</c> and must not record again, while one that does not (the
    /// database, APNs, App Insights) records for itself. Double-recording would count one failure
    /// twice.
    /// </para>
    ///
    /// <para>
    /// It never throws for a source that is simply unreachable — that is
    /// <see cref="DataSourceOutcome.Failed"/>, which is an answer, not an error.
    /// </para>
    /// </summary>
    public interface IDataSourceProbe
    {
        /// <summary>The catalog key this probe answers for.</summary>
        string Key { get; }

        Task<DataSourceOutcome> ProbeAsync(CancellationToken ct);
    }
}
