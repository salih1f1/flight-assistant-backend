namespace FlightAssistant.Application.DataSources
{
    /// <summary>
    /// The status a source's row displays. <see cref="Unknown"/> is first-class: a source that has
    /// never been observed must not render as healthy.
    /// </summary>
    public enum DataSourceStatusValue { Unknown, NotConfigured, Ok, Degraded, Unhealthy }
}
