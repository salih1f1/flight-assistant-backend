using System.ComponentModel.DataAnnotations.Schema;

namespace FlightAssistant.Domain.Common
{
    public class BaseEntity
    {
        public Guid Id { get; protected set; } = Guid.NewGuid();
        
        private readonly List<DomainEvent> _domainEvents = new();

        [NotMapped]
        public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

        protected void Raise(DomainEvent @event) => _domainEvents.Add(@event);
        public void ClearDomainEvents() => _domainEvents.Clear();

        public override bool Equals(object? obj) =>
            obj is BaseEntity other && Id != Guid.Empty && other.Id == Id;

        public override int GetHashCode() => Id.GetHashCode();
    }
}
