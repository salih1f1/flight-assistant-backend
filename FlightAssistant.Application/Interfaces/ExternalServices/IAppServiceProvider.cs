using FlightAssistant.Application.Hosting;

namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// Reads the App Service the API is hosted in, from ARM.
    ///
    /// <para>
    /// Never throws for an expected condition. Not running on App Service, a refusal, an outage —
    /// each maps to an <see cref="AppServiceState"/>, because every one of them is something the
    /// panel must be able to say out loud rather than render as an error.
    /// </para>
    /// </summary>
    public interface IAppServiceProvider
    {
        Task<AppServiceSnapshot> GetStatusAsync(CancellationToken ct);
    }
}
