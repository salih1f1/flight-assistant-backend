using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Flights
{
    public sealed class FlightStatusConfiguration : IEntityTypeConfiguration<FlightStatus>
    {
        public void Configure(EntityTypeBuilder<FlightStatus> b)
        {
            b.ToTable("flight_statuses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).IsRequired().HasMaxLength(32);
            b.Property(x => x.Name).IsRequired().HasMaxLength(64);
            b.Property(x => x.Description).HasMaxLength(4000);

            b.HasIndex(x => x.Code).IsUnique();

            // Seeds the fixed vocabulary emitted by AeroApiFlightStatusProvider.MapStatusCode —
            // the sole flight-status provider. Without this, SyncFlightStatusCommand's lookup of
            // a provider status code against this table always misses on a fresh database, and
            // throws only *after* the (billable) AeroAPI call has already been made (#281).
            //
            // HasData both generates the InsertData in the migration and records the rows in the
            // model snapshot, so the seed can't drift between what a fresh migration creates and
            // what the compiled model expects.
            //
            // Ids are explicit and stable by necessity: FlightStatusEvent.StatusId is a foreign
            // key into this table, so renumbering or reusing an id here would silently repoint
            // historical events at a different status. NEVER change an existing row's Id — add
            // new codes with new, higher ids instead.
            //
            // Because these are explicit-id inserts into an identity column, the Postgres
            // sequence backing Id is NOT advanced by them. That's acceptable here: this table is
            // a fixed reference vocabulary that nothing at runtime ever inserts into (unlike, say,
            // traveler-authored rows), so there is no future insert that could collide with these
            // ids.
            b.HasData(
                new { Id = 1, Code = "SCH", Name = "Scheduled", Description = "Flight is scheduled and has not yet departed." },
                new { Id = 2, Code = "DEP", Name = "Departed", Description = "Flight has departed and is en route to its destination." },
                new { Id = 3, Code = "ARR", Name = "Arrived", Description = "Flight has arrived at the destination gate." },
                new { Id = 4, Code = "DLY", Name = "Delayed", Description = "Flight is delayed from its scheduled departure or arrival time." },
                new { Id = 5, Code = "CAN", Name = "Cancelled", Description = "Flight has been cancelled." },
                new { Id = 6, Code = "DIV", Name = "Diverted", Description = "Flight has been diverted to an airport other than its scheduled destination." },
                new { Id = 7, Code = "UNK", Name = "Unknown", Description = "Status could not be determined, or the provider reported a status this system does not recognise." }
            );
        }
    }
}
