using FlightAssistant.Application.Usage;

namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// Reads the subscription's budget and month-to-date spend from Azure Cost Management.
    ///
    /// <para>
    /// Never throws for an expected condition. A missing subscription id, a refusal, an outage —
    /// each maps to an <see cref="AzureBudgetState"/>, because every one of them is something the
    /// panel must be able to say out loud rather than render as an error or, worse, as zero spend.
    /// </para>
    /// </summary>
    public interface IAzureCostProvider
    {
        Task<AzureBudgetSnapshot> GetBudgetAsync(CancellationToken ct);
    }
}
