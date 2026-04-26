namespace FlightAssistant.Infrastructure.Notifications
{
    public sealed class ApnsOptions
    {
        public string? BundleId { get; set; }
        public string? KeyId { get; set; }
        public string? TeamId { get; set; }
        public string? P8KeyContent { get; set; }
        public bool UseSandbox { get; set; }
    }
}
