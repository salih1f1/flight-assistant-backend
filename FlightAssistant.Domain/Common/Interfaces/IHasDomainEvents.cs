namespace FlightAssistant.Domain.Common.Interfaces
{
    /// <summary>
    /// Implemented by any entity that can raise domain events.
    /// Used by the EF SaveChanges interceptor to collect and dispatch events
    /// after a successful commit — regardless of whether the entity uses a Guid
    /// or an int primary key.
    /// </summary>
    public interface IHasDomainEvents
    {
        IReadOnlyCollection<DomainEvent> DomainEvents { get; }
        void ClearDomainEvents();
    }
}
