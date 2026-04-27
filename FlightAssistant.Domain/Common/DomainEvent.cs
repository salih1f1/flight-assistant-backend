using MediatR;

namespace FlightAssistant.Domain.Common
{
    /// <summary>
    /// Base for all domain events. Implements <see cref="INotification"/> so that
    /// events can be published directly via MediatR's <see cref="IPublisher"/> from
    /// the EF SaveChanges interceptor.
    /// </summary>
    public abstract record DomainEvent(DateTime OccurredUtc) : INotification
    {
        public string? CorrelationId { get; init; }
        public string? CausationId { get; init; }
    }
}
