using FlightAssistant.Domain.Administration.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Administration
{
    public sealed class AdminAuditLogConfiguration : IEntityTypeConfiguration<AdminAuditLog>
    {
        public void Configure(EntityTypeBuilder<AdminAuditLog> b)
        {
            b.ToTable("admin_audit_log");
            b.HasKey(x => x.Id);

            b.Property(x => x.ActorId).IsRequired().HasMaxLength(200);
            b.Property(x => x.ActorName).HasMaxLength(200);

            b.Property(x => x.ActionUtc).IsRequired().HasColumnType("timestamptz");

            // Stored as a string, not the underlying int: an int in an audit table is
            // unreadable in a psql session and renumbers silently if the enum is ever reordered.
            b.Property(x => x.Action)
                .IsRequired()
                .HasMaxLength(32)
                .HasConversion<string>();

            b.Property(x => x.EntityType).IsRequired().HasMaxLength(200);

            // Nullable: a store-generated key (e.g. an identity column) does not exist yet at
            // SavingChanges time, and the interceptor records that honestly as null rather than
            // inventing a value (see AdminAuditLog.EntityId and AdminAuditInterceptor.GetEntityId).
            b.Property(x => x.EntityId).HasMaxLength(200);

            b.Property(x => x.Summary).IsRequired().HasMaxLength(1000);

            b.Property(x => x.Before).HasColumnType("jsonb");
            b.Property(x => x.After).HasColumnType("jsonb");

            b.Property(x => x.TraceId).HasMaxLength(100);

            // The read endpoint's default sort (Task 3).
            b.HasIndex(x => x.ActionUtc)
                .IsDescending()
                .HasDatabaseName("ix_admin_audit_log_action_utc");

            // "History of this row" lookup (Task 3).
            b.HasIndex(x => new { x.EntityType, x.EntityId })
                .HasDatabaseName("ix_admin_audit_log_entity");
        }
    }
}
