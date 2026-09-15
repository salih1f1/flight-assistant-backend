namespace FlightAssistant.Application.ProviderBudget
{
    /// <summary>Where this month's AeroAPI spend stands against <c>AeroApi:MonthlyBudgetUsd</c> (#303).</summary>
    public enum ProviderBudgetState
    {
        /// <summary>Below the soft limit, or the spend couldn't be read.</summary>
        Normal,

        /// <summary>At or past <c>AeroApi:SoftLimitRatio</c> of the budget: only cached searches are
        /// answered, and Live polls run at twice their interval.</summary>
        SoftLimit,

        /// <summary>At or past the budget: only a flight in Fast mode makes a call.</summary>
        Exhausted
    }

    /// <summary>
    /// Says how hard to hold back AeroAPI calls this month (#303). The spend comes from AeroAPI's own
    /// usage report, cached 10 minutes and shared with the admin usage gauge, so a check makes no metered call.
    /// </summary>
    public interface IProviderBudgetGuard
    {
        Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default);
    }
}
