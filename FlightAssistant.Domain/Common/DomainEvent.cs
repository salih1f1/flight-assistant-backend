namespace FlightAssistant.Domain.Common
{
    public abstract record DomainEvent(DateTime OccurredUtc)
    {
        public string? CorrelationId { get; init; }
        public string? CausationId { get; init; }
    }
}
