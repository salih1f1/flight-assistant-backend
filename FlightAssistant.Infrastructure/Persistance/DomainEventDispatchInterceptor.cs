using FlightAssistant.Domain.Common.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlightAssistant.Infrastructure.Persistance
{
    /// <summary>
    /// After a successful <see cref="SaveChangesAsync"/>, collects all domain events
    /// from tracked entities that implement <see cref="IHasDomainEvents"/> and
    /// publishes them via MediatR. Events are cleared from the entity before publishing
    /// so a retry of SaveChanges doesn't re-fire them.
    ///
    /// This guarantees the "no notification if SaveChanges fails" acceptance criterion:
    /// events are only dispatched after the database write commits.
    /// </summary>
    public sealed class DomainEventDispatchInterceptor : SaveChangesInterceptor
    {
        private readonly IPublisher _publisher;

        public DomainEventDispatchInterceptor(IPublisher publisher)
        {
            _publisher = publisher;
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            if (context is null) return result;

            // Collect all domain events from any tracked entity that raises them
            var events = context.ChangeTracker
                .Entries<IHasDomainEvents>()
                .SelectMany(e => e.Entity.DomainEvents)
                .ToList();

            // Clear before publishing — prevents double-dispatch on nested SaveChanges
            foreach (var entry in context.ChangeTracker.Entries<IHasDomainEvents>())
                entry.Entity.ClearDomainEvents();

            foreach (var domainEvent in events)
                await _publisher.Publish(domainEvent, cancellationToken);

            return result;
        }
    }
}
