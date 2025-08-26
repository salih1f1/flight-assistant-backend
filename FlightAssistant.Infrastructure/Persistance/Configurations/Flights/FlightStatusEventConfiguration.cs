using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Flights
{
    public sealed class FlightStatusEventConfiguration : IEntityTypeConfiguration<FlightStatusEvent>
    {
        public void Configure(EntityTypeBuilder<FlightStatusEvent> b)
        {
            b.ToTable("flight_status_events");

            b.HasKey(x => x.Id);
            b.Property(x => x.Id);

            b.Property(x => x.FlightId).IsRequired();
            b.Property(x => x.StatusId).IsRequired();

            b.Property(x => x.EffectiveAtUtc).HasColumnType("timestamptz");
            b.Property(x => x.RecordedAtUtc).HasColumnType("timestamptz");

            b.Property(x => x.Source)
                .IsRequired()
                .HasMaxLength(400)
                .HasDefaultValue("manual");

            b.Property(x => x.ExternalId).HasMaxLength(64);
            b.Property(x => x.Gate).HasMaxLength(10);
            b.Property(x => x.Terminal).HasMaxLength(10);
            b.Property(x => x.BaggageClaim).HasMaxLength(20);
            b.Property(x => x.Remarks);
            b.Property(x => x.RawPayloadJson).HasColumnType("jsonb"); // allows indexing/search if needed

            b.Property(x => x.DedupHash)
                .IsRequired()
                .HasMaxLength(64);

            b.HasOne(x => x.Flight)
                .WithMany()
                .HasForeignKey(x => x.FlightId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Status)
                .WithMany()
                .HasForeignKey(x => x.StatusId)
                .OnDelete(DeleteBehavior.Restrict);

            // Indexes
            // Latest status scan patterns
            b.HasIndex(x => new { x.FlightId, x.EffectiveAtUtc, x.RecordedAtUtc })
             .HasDatabaseName("ix_fse_latest");

            b.HasIndex(x => new { x.FlightId, x.StatusId, x.EffectiveAtUtc })
             .HasDatabaseName("ix_fse_status_time");

            // Provider de-duplication
            b.HasIndex(x => new { x.FlightId, x.Source, x.DedupHash })
             .IsUnique();
        }
    }
}
