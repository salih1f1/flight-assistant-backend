namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <param name="ObservedUtc">When this reading was taken. The figure is only as fresh as the last
    /// call this process made, and the panel says so rather than implying it is live.</param>
    public sealed record ProviderQuota(int Remaining, int Limit, DateTimeOffset ObservedUtc);

    /// <summary>
    /// Provider-reported quota, read off response headers rather than counted locally — the same rule
    /// that keeps AeroAPI spend coming from <c>/account/usage</c> and never from a local meter.
    /// </summary>
    public interface IProviderQuotaReader
    {
        /// <summary>
        /// The last quota AeroDataBox reported to <b>this process</b>, or null when it has made no
        /// call yet. Null means "cannot tell", never "nothing left", and it is per instance: a
        /// scaled-out deployment shows whichever instance served the request.
        /// </summary>
        ProviderQuota? AeroDataBox { get; }
    }
}
