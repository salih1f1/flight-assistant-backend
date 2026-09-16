using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Verification
{
    /// <summary>
    /// The <c>verification_requests</c> queue table (#367, spec §2.3). Column names are snake_case
    /// because <c>VerificationRequestRepository</c>'s claim SQL names them directly.
    /// </summary>
    public sealed class VerificationRequestConfiguration : IEntityTypeConfiguration<VerificationRequest>
    {
        /// <summary>
        /// At most one open request per entity. <c>VerificationRequestRepository</c> matches this
        /// name to tell a lost enqueue race (counted as skipped) from any other unique violation.
        /// </summary>
        public const string OpenRequestIndexName = "ux_verification_requests_open_entity";

        /// <summary>The claim query's supporting index.</summary>
        public const string ClaimIndexName = "ix_verification_requests_claim";

        /// <summary>
        /// The error-cooldown lookup's supporting index (#418): for a batch of entity keys, which
        /// ones have an <see cref="VerificationRequestStatus.Error"/> request that completed at or
        /// after some cutoff <em>and</em> is still the most recent completion for that key. Partial
        /// like <see cref="OpenRequestIndexName"/> — <c>verification_requests</c> is never purged, so
        /// an unfiltered index would keep costing more every day. Filtered on <c>completed_utc IS NOT
        /// NULL</c>, not <c>status = 'Error'</c> (fix round 1): the lookup's anti-join needs "is there
        /// a newer completion of any status", which an Error-only partial index cannot serve —
        /// <c>completed_utc IS NOT NULL</c> covers every completed row and so serves both halves of
        /// the query from one index.
        /// </summary>
        public const string ErrorCooldownIndexName = "ix_verification_requests_error_cooldown";

        /// <summary>
        /// Shadow property mapped to PostgreSQL's <c>xmin</c> system column: the row's concurrency
        /// token. The claim SQL has to return <c>xmin</c> explicitly, because <c>RETURNING *</c>
        /// never includes system columns.
        /// </summary>
        public const string RowVersionPropertyName = "RowVersion";

        /// <summary>Stored value of <see cref="VerificationTrigger.Admin"/>.</summary>
        public const string AdminTriggerValue = "admin";

        /// <summary>Stored value of <see cref="VerificationTrigger.Flight"/>.</summary>
        public const string FlightTriggerValue = "flight";

        public void Configure(EntityTypeBuilder<VerificationRequest> b)
        {
            b.ToTable("verification_requests");
            b.HasKey(x => x.Id);

            b.Property(x => x.Id).HasColumnName("id");

            // Enum-like columns hold names, not ints, so a psql session can read them and the
            // claim SQL and the partial index can filter on them.
            b.Property(x => x.EntityType)
                .HasColumnName("entity_type")
                .IsRequired()
                .HasMaxLength(50)
                .HasConversion<string>();

            b.Property(x => x.EntityKey).HasColumnName("entity_key").IsRequired().HasMaxLength(VerificationRequest.MaxEntityKeyLength);
            b.Property(x => x.Sources).HasColumnName("sources").IsRequired().HasMaxLength(VerificationRequest.MaxSourcesLength);

            // Lower-case (spec §2.3), unlike the other enum columns, so it needs an explicit mapping.
            b.Property(x => x.Trigger)
                .HasColumnName("trigger")
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion(t => TriggerToStorage(t), v => TriggerFromStorage(v));

            b.Property(x => x.Status)
                .HasColumnName("status")
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion<string>();

            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.NextAttemptUtc).HasColumnName("next_attempt_utc").HasColumnType("timestamptz");

            b.Property(x => x.DifferencesJson).HasColumnName("differences").HasColumnType("jsonb");
            b.Property(x => x.SourceValuesJson).HasColumnName("source_values").HasColumnType("jsonb");

            b.Property(x => x.Error).HasColumnName("error").HasMaxLength(VerificationRequest.MaxErrorLength);
            b.Property(x => x.EstimatedCostUsd).HasColumnName("estimated_cost_usd").HasPrecision(10, 4);
            b.Property(x => x.RequestedBy).HasColumnName("requested_by").IsRequired().HasMaxLength(VerificationRequest.MaxRequestedByLength);

            b.Property(x => x.RequestedUtc).HasColumnName("requested_utc").HasColumnType("timestamptz");
            b.Property(x => x.StartedUtc).HasColumnName("started_utc").HasColumnType("timestamptz");
            b.Property(x => x.CompletedUtc).HasColumnName("completed_utc").HasColumnType("timestamptz");

            // Optimistic concurrency (#367): xmin changes on every write to the row, so a save made
            // from a claim that has since been reset as stuck and claimed again matches no row and
            // throws DbUpdateConcurrencyException, instead of overwriting the newer claim. xmin
            // exists on every PostgreSQL table, so this needs no DDL.
            b.Property<uint>(RowVersionPropertyName).IsRowVersion();

            b.HasIndex(x => new { x.EntityType, x.EntityKey })
                .IsUnique()
                .HasFilter($"\"status\" IN ('{nameof(VerificationRequestStatus.Pending)}', '{nameof(VerificationRequestStatus.Running)}')")
                .HasDatabaseName(OpenRequestIndexName);

            b.HasIndex(x => new { x.Status, x.NextAttemptUtc })
                .HasDatabaseName(ClaimIndexName);

            // EntityType first (every lookup names exactly one), then EntityKey (the batch's keys are
            // matched with an IN, i.e. one equality per key), then CompletedUtc last so Postgres can
            // apply the cutoff as an index condition within each per-key scan rather than a filter
            // after the fact.
            //
            // Filtered on "completed_utc" IS NOT NULL, not "status" = 'Error' (#418 fix round 1): the
            // cooldown lookup's anti-join half asks "is there a newer completion of *any* status for
            // this key", which a status='Error' partial index cannot legally serve — EXPLAIN against
            // 248k seeded rows proved Postgres falls back to a full sequential scan for that half
            // (4065 of 4073 buffers), paid only when a batch actually contains a cooling key, i.e.
            // precisely the upstream-outage scenario #418 exists to make cheap, and growing forever
            // since verification_requests is never purged. completed_utc IS NOT NULL serves both
            // halves of VerificationRequestRepository.EnqueueAsync's cooldown query from the one
            // index: the Error half gets the (entity_type, entity_key) prefix plus the completed_utc
            // range, then a cheap heap check for status bounded by one entity's row count (~10), not
            // table size; the newer-completion half gets the same prefix and range with no extra
            // check at all. Both halves of that query state "CompletedUtc != null" explicitly so the
            // planner can prove the partial index applies — keep that condition even though every
            // Error row already has a CompletedUtc, and even though a null CompletedUtc can't equal or
            // exceed a cutoff anyway.
            b.HasIndex(x => new { x.EntityType, x.EntityKey, x.CompletedUtc })
                .HasFilter("\"completed_utc\" IS NOT NULL")
                .HasDatabaseName(ErrorCooldownIndexName);
        }

        /// <summary>The stored value for <paramref name="trigger"/>.</summary>
        public static string TriggerToStorage(VerificationTrigger trigger) => trigger switch
        {
            VerificationTrigger.Admin => AdminTriggerValue,
            VerificationTrigger.Flight => FlightTriggerValue,
            _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown verification trigger.")
        };

        /// <summary>The trigger a stored value represents.</summary>
        public static VerificationTrigger TriggerFromStorage(string value) => value switch
        {
            AdminTriggerValue => VerificationTrigger.Admin,
            FlightTriggerValue => VerificationTrigger.Flight,
            _ => throw new InvalidOperationException($"Unknown verification trigger '{value}' in verification_requests.")
        };
    }
}
