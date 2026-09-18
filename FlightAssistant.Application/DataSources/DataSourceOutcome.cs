namespace FlightAssistant.Application.DataSources
{
    /// <summary>
    /// What one observation of a source saw. <see cref="Unauthorized"/> is split out from
    /// <see cref="Failed"/> deliberately: it is the state the old AeroAPI health check reported as
    /// Healthy ("Reachable — HTTP 401"), and it needs a different remedy (rotate a key) from a
    /// network failure (wait). <see cref="RateLimited"/> is likewise not a fault of ours.
    /// </summary>
    public enum DataSourceOutcome { Ok, Failed, Unauthorized, RateLimited, NotConfigured }
}
