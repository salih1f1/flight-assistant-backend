using System.ComponentModel.DataAnnotations.Schema;
using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Domain.Common
{
    public abstract class BaseIntEntity : IHasDomainEvents
    {
        public int Id { get; private set; }

        private readonly List<DomainEvent> _domainEvents = new();

        [NotMapped]
        public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

        protected void Raise(DomainEvent domainEvent) => _domainEvents.Add(domainEvent);

        public void ClearDomainEvents() => _domainEvents.Clear();

        public override bool Equals(object? obj)
        {
            if (obj is not BaseIntEntity other) return false;
            if (ReferenceEquals(this, other)) return true;
            return Id == other.Id && GetType() == other.GetType();
        }

        public override int GetHashCode() => HashCode.Combine(GetType(), Id);
    }
}
