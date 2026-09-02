using System.Diagnostics;
using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlightAssistant.Infrastructure.Persistance
{
    /// <summary>
    /// Writes one <see cref="AdminAuditLog"/> row per admin write, inside the very
    /// <c>SaveChanges</c> call — and therefore the very database transaction — that performs the
    /// write it describes (issue #313, Task 2).
    ///
    /// <para>
    /// <b>Why this overrides <see cref="SavingChanges"/>/<see cref="SavingChangesAsync"/>, which
    /// run before the write is sent, and not <c>SavedChanges</c>/<c>SavedChangesAsync</c> the way
    /// its neighbour <see cref="DomainEventDispatchInterceptor"/> does.</b> That asymmetry is
    /// deliberate, not a copy/paste miss. <see cref="DomainEventDispatchInterceptor"/> fires
    /// after commit because a domain event must never be published for a write that turned out
    /// not to happen. An audit row carries the opposite risk: one that can commit — or fail —
    /// independently of the write it describes is worse than no audit row at all, because it
    /// would either record a write the database then rolled back, or the audit write could itself
    /// fail after the real write already committed unaudited. Adding the
    /// <see cref="AdminAuditLog"/> entity here, before <c>SaveChanges</c> sends anything to the
    /// database, is what puts it in the *same* transaction as the write it describes and lets a
    /// database failure roll both back together.
    /// </para>
    /// </summary>
    public sealed class AdminAuditInterceptor : SaveChangesInterceptor
    {
        // Derived via reflection against the interface, not a hard-coded string list, so a rename
        // of an IVerifiable member can't silently desync this from the Verify-detection rule.
        private static readonly HashSet<string> VerifiablePropertyNames = typeof(IVerifiable)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        private readonly IAdminContext _adminContext;

        public AdminAuditInterceptor(IAdminContext adminContext)
        {
            _adminContext = adminContext;
        }

        // Supported, not just tolerated: every step below is synchronous already (no I/O), so a
        // caller that saves via the sync SaveChanges() overload gets exactly the same audit
        // behaviour as SaveChangesAsync() rather than a silently-skipped audit row.
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            WriteAuditRows(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            WriteAuditRows(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private void WriteAuditRows(DbContext? context)
        {
            // Cheap, expected case — EF can raise this event with no context attached.
            if (context is null) return;

            // No actor ⇒ no row, immediately. Every ordinary traveler write goes through this
            // same DbContext and this same interceptor; IAdminContext.ActorId is only ever
            // populated for the admin endpoint group (see the Program.cs endpoint filter), so an
            // unset actor means this save has nothing to do with an admin write.
            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId)) return;

            // Snapshot before adding anything — enumerating ChangeTracker.Entries() while adding
            // newly-tracked entities to the same tracker is how this turns into an infinite loop.
            var entries = context.ChangeTracker.Entries().ToList();

            // Append-only: nothing may edit or remove an audit row, ever.
            var illegalChange = entries.FirstOrDefault(e =>
                e.Metadata.ClrType == typeof(AdminAuditLog) &&
                e.State is EntityState.Modified or EntityState.Deleted);
            if (illegalChange is not null)
            {
                throw new InvalidOperationException(
                    $"AdminAuditLog rows are append-only. An entry was {illegalChange.State}; " +
                    "only Added is permitted for this entity.");
            }

            var actionUtc = DateTime.UtcNow; // one instant shared by every row written in this save
            var traceId = Activity.Current?.Id;

            foreach (var entry in entries)
            {
                // Skip the audit log's own type — auditing an audit row is how this recurses.
                if (entry.Metadata.ClrType == typeof(AdminAuditLog)) continue;

                // Ignore Unchanged and Detached entirely.
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

                context.Add(BuildAuditLog(entry, actorId, actionUtc, traceId));
            }
        }

        private AdminAuditLog BuildAuditLog(EntityEntry entry, string actorId, DateTime actionUtc, string? traceId)
        {
            // entry.Metadata.ClrType, never entry.Entity.GetType(): a proxy or derived runtime
            // type would miss the SensitiveProperties registry lookup, and a missed lookup here is
            // a PII leak, not a cosmetic bug.
            var clrType = entry.Metadata.ClrType;

            // IAdminContext.ActionOverride, when set, wins over the derivation below. Only the CSV
            // import endpoint filter sets it (Program.cs), since bulk upserts are otherwise
            // indistinguishable from an ordinary create/update.
            var action = _adminContext.ActionOverride ?? DeriveAction(entry);

            string? before = null;
            string? after = null;

            switch (entry.State)
            {
                case EntityState.Added:
                    after = SerializeProperties(entry, clrType, useOriginalValue: false, onlyModified: false);
                    break;
                case EntityState.Deleted:
                    before = SerializeProperties(entry, clrType, useOriginalValue: true, onlyModified: false);
                    break;
                case EntityState.Modified:
                    before = SerializeProperties(entry, clrType, useOriginalValue: true, onlyModified: true);
                    after = SerializeProperties(entry, clrType, useOriginalValue: false, onlyModified: true);
                    break;
            }

            var entityId = GetEntityId(entry);

            // EntityId and EntityType can never be sensitive values (they come from primary-key
            // metadata and the CLR type name, never a registered property), so the summary needs
            // no masking pass of its own to satisfy the "no sensitive value in Summary" rule.
            var summary = $"{VerbFor(action)} {clrType.Name} {entityId}";

            return AdminAuditLog.Create(
                actorId: actorId,
                actorName: _adminContext.ActorName,
                action: action,
                entityType: clrType.Name,
                entityId: entityId,
                summary: summary,
                before: before,
                after: after,
                traceId: traceId,
                actionUtc: actionUtc);
        }

        private static AdminAuditAction DeriveAction(EntityEntry entry) => entry.State switch
        {
            EntityState.Added => AdminAuditAction.Create,
            EntityState.Deleted => AdminAuditAction.Delete,
            // A Modified entry whose set of modified properties is exactly the IVerifiable trio is
            // a Verify; verifying *and* changing another property in the same save is correctly an
            // Update, since the modified-property set is then a superset, not an exact match.
            EntityState.Modified => IsVerifyOnly(entry) ? AdminAuditAction.Verify : AdminAuditAction.Update,
            _ => throw new InvalidOperationException(
                $"AdminAuditInterceptor cannot derive an action for entity state {entry.State}; " +
                "callers must filter to Added/Modified/Deleted before reaching this.")
        };

        private static bool IsVerifyOnly(EntityEntry entry)
        {
            var modified = entry.Properties
                .Where(p => p.IsModified)
                .Select(p => p.Metadata.Name)
                .ToHashSet(StringComparer.Ordinal);

            return modified.SetEquals(VerifiablePropertyNames);
        }

        /// <summary>
        /// Before/After carry only the changed properties, never whole entities — navigation
        /// properties are never included because <see cref="EntityEntry.Properties"/> only
        /// enumerates scalar/FK properties in the first place. Any value whose property name is
        /// registered in <see cref="SensitiveProperties"/> is replaced with the literal string
        /// <c>"&lt;redacted&gt;"</c> — never the real value, never a partial one.
        /// </summary>
        private static string SerializeProperties(EntityEntry entry, Type clrType, bool useOriginalValue, bool onlyModified)
        {
            var properties = onlyModified ? entry.Properties.Where(p => p.IsModified) : entry.Properties;

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                var name = property.Metadata.Name;
                values[name] = SensitiveProperties.IsSensitive(clrType, name)
                    ? "<redacted>"
                    : (useOriginalValue ? property.OriginalValue : property.CurrentValue);
            }

            return JsonSerializer.Serialize(values);
        }

        /// <summary>
        /// The entry's primary key via EF metadata, never a hand-picked "display" column.
        /// Composite keys join their parts with <c>|</c>. A store-generated key (e.g. an int
        /// identity column) on an <see cref="EntityState.Added"/> entry may still be unset at
        /// SavingChanges time — this records whatever value is actually present rather than
        /// inventing one.
        /// </summary>
        private static string GetEntityId(EntityEntry entry)
        {
            var key = entry.Metadata.FindPrimaryKey();
            if (key is null) return string.Empty;

            return string.Join("|", key.Properties.Select(p =>
                entry.Property(p.Name).CurrentValue?.ToString() ?? string.Empty));
        }

        private static string VerbFor(AdminAuditAction action) => action switch
        {
            AdminAuditAction.Create => "Created",
            AdminAuditAction.Update => "Updated",
            AdminAuditAction.Delete => "Deleted",
            AdminAuditAction.Verify => "Verified",
            AdminAuditAction.Import => "Imported",
            AdminAuditAction.Reveal => "Revealed",
            _ => action.ToString()
        };
    }
}
