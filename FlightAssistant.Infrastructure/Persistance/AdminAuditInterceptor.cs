using System.Diagnostics;
using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.DataSources.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Verification.Entities;
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
    ///
    /// <para>
    /// <b>Hazard: a retried <c>SaveChanges</c> on the same context double-audits.</b> After a
    /// failed save, the write's entities stay <c>Added</c>/<c>Modified</c>/<c>Deleted</c> and the
    /// audit row already staged for them stays tracked too. A second <c>SaveChangesAsync()</c> on
    /// that same context re-enters <see cref="WriteAuditRows"/>, which happily skips the
    /// already-tracked audit rows (see the loop below) but re-derives and adds a *new* one for
    /// each original entry — two audit rows for one logical write. This is latent, not live,
    /// today: <c>UseNpgsql</c> is configured with no <c>EnableRetryOnFailure</c>, and nothing in
    /// this codebase retries a failed <c>SaveChanges</c> call. If a retrying execution strategy is
    /// ever enabled, this becomes real. Accepted rather than guarded against, because the failure
    /// mode is over-auditing (a duplicate row), not under-auditing (a missing one) — the safe
    /// direction for an audit trail to fail in (#313 Task 2 review, F8/M8).
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

        // Entity types this interceptor never writes a row for.
        // - AdminAuditLog: auditing an audit row is how this recurses.
        // - VerificationRequest (#367): the queue table is its own history (spec §4.5). Auditing it
        //   under the verification job's system actor would add an Update row per status change of
        //   every request. The job's writes to the entities it checks are still audited.
        // - DataSourceStatus (#460): written by the buffered health observer on a background flush, not
        //   by an admin. Auditing it would put a row in the audit log for every outbound HTTP call the
        //   application makes. The one operator action against this table — pressing "Check now" — writes
        //   its own audit row explicitly in ProbeDataSourceCommandHandler, so the deliberate act is
        //   audited while the automatic bookkeeping is not.
        // The append-only guard below is separate and still covers AdminAuditLog only.
        private static readonly HashSet<Type> NotAuditedTypes = new()
        {
            typeof(AdminAuditLog),
            typeof(VerificationRequest),
            typeof(DataSourceStatus)
        };

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

            // Snapshot before adding anything — enumerating ChangeTracker.Entries() while adding
            // newly-tracked entities to the same tracker is how this turns into an infinite loop.
            var entries = context.ChangeTracker.Entries().ToList();

            // Append-only: nothing may edit or remove an audit row, ever — checked *before* the
            // actor gate below, deliberately. This guard used to sit after the actor check, which
            // meant it only ever fired on an admin-actor save; any save with no actor (every
            // ordinary traveler write, every hosted service, every test helper, any future
            // background job) went straight past it, so a bug anywhere outside the admin surface
            // could silently rewrite or delete an audit row with no actor ever set. Tamper-evidence
            // is the whole reason this log is transactional, so it cannot depend on who — or
            // whether anyone — is saving. (issue #313 Task 2 review, F1.)
            var illegalChange = entries.FirstOrDefault(e =>
                e.Metadata.ClrType == typeof(AdminAuditLog) &&
                e.State is EntityState.Modified or EntityState.Deleted);
            if (illegalChange is not null)
            {
                throw new InvalidOperationException(
                    $"AdminAuditLog rows are append-only. An entry was {illegalChange.State}; " +
                    "only Added is permitted for this entity.");
            }

            // No actor ⇒ no row, immediately. Every ordinary traveler write goes through this
            // same DbContext and this same interceptor; IAdminContext.ActorId is only ever
            // populated for the admin endpoint group (see the Program.cs endpoint filter), so an
            // unset actor means this save has nothing to do with an admin write.
            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId)) return;

            var actionUtc = DateTime.UtcNow; // one instant shared by every row written in this save

            // CorrelationIdMiddleware's id (#319) is primary: it's the one identifier guaranteed to
            // also be on the response header and every Serilog line for this request, populated
            // onto IAdminContext by the /admin endpoint filter (Program.cs) before this interceptor
            // ever runs. Activity.Current is a fallback for the rare save that reaches here with an
            // actor set but no CorrelationId populated (e.g. a caller that builds an AdminContext by
            // hand rather than through the real request pipeline), not the expected path.
            var traceId = _adminContext.CorrelationId ?? Activity.Current?.Id;

            foreach (var entry in entries)
            {
                // Skip the types this log never records (see NotAuditedTypes).
                if (NotAuditedTypes.Contains(entry.Metadata.ClrType)) continue;

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
            // entityId is null only for a store-generated-key create (see GetEntityId) — rendered
            // as "(pending id)" rather than an empty trailing space so the summary line still
            // reads sensibly; the full created row is still recoverable from After.
            var summary = $"{VerbFor(action)} {clrType.Name} {entityId ?? "(pending id)"}";

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
            // A Modified entry whose modified-property set is non-empty and entirely contained in
            // the IVerifiable set is a Verify; verifying *and* changing another property in the
            // same save is correctly an Update, since the modified-property set then contains a
            // property outside IVerifiable.
            EntityState.Modified => IsVerifyOnly(entry) ? AdminAuditAction.Verify : AdminAuditAction.Update,
            _ => throw new InvalidOperationException(
                $"AdminAuditInterceptor cannot derive an action for entity state {entry.State}; " +
                "callers must filter to Added/Modified/Deleted before reaching this.")
        };

        // Non-empty and a subset of the IVerifiable property set — not an exact-match SetEquals.
        // VerificationIssue stays null->null on an ordinary Verify()/Unverify() call (it was
        // already null and Verify/Unverify leave it null), so EF never marks it modified; an
        // exact-match rule would then miss the IVerifiable set on every such save and log it as
        // an Update instead of a Verify. MarkVerificationFailed's own save is still correctly
        // detected: it touches IsVerified/VerifiedAt/VerifiedBy/VerificationSource/VerificationIssue,
        // still a (now full) subset. The empty-set guard matters because an empty modified set is
        // vacuously a subset of anything — without it, a save that has actually modified nothing
        // on this entry (Unchanged filtered out already, but a Modified entry can still carry zero
        // changed *properties* in edge cases) would wrongly report Verify rather than falling
        // through as it should.
        private static bool IsVerifyOnly(EntityEntry entry)
        {
            var modified = entry.Properties
                .Where(p => p.IsModified)
                .Select(p => p.Metadata.Name)
                .ToHashSet(StringComparer.Ordinal);

            return modified.Count > 0 && modified.IsSubsetOf(VerifiablePropertyNames);
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
                    ? SensitiveProperties.RedactedPlaceholder
                    : (useOriginalValue ? property.OriginalValue : property.CurrentValue);
            }

            return JsonSerializer.Serialize(values);
        }

        /// <summary>
        /// The entry's primary key via EF metadata, never a hand-picked "display" column.
        /// Composite keys join their parts with <c>|</c>.
        ///
        /// <para>
        /// A store-generated key (e.g. an int identity column, <c>ValueGeneratedOnAdd</c>) on an
        /// <see cref="EntityState.Added"/> entry is genuinely unset at <c>SavingChanges</c> time —
        /// Npgsql assigns the real value during <c>SaveChanges</c> via <c>INSERT … RETURNING</c>,
        /// which runs *after* this interceptor. EF already knows this: it stamps such a property
        /// with a placeholder (a negative sentinel for an <c>int</c>) purely for in-memory
        /// relationship fixup, and marks the <see cref="PropertyEntry.IsTemporary"/> flag on it —
        /// this method treats that flag as the source of truth, not the value itself, which is why
        /// this records <c>null</c> rather than that placeholder (or the CLR default, e.g.
        /// <c>"0"</c>). Either of those would look like real data and collide across every create
        /// of every store-generated-key type, so filtering the audit log on it would return every
        /// such create ever recorded — worse than admitting the id isn't known yet. Recovering the
        /// real id would mean moving this write to run after the INSERT, which forfeits the
        /// same-transaction guarantee the whole interceptor exists for (see the class comment).
        /// The created entity stays identifiable through <see cref="AdminAuditLog.After"/>, which
        /// carries its full property set regardless of whether the key made it in.
        /// </para>
        /// </summary>
        private static string? GetEntityId(EntityEntry entry)
        {
            var key = entry.Metadata.FindPrimaryKey();
            // A keyless entity (no HasNoKey mapping exists in this codebase today — see #313
            // final review, M7) has no primary key to stringify at all, which is a different case
            // from "not yet assigned": null is the honest value here too, exactly like the
            // temporary-key branch two lines below, and lets AdminAuditLog.Create succeed instead
            // of rejecting an empty string and turning an audit attempt into a failed admin write.
            if (key is null) return null;

            var properties = key.Properties.Select(p => entry.Property(p.Name)).ToList();

            // Any part still temporary (store-generated, not yet assigned by the database) makes
            // the whole id unknown rather than partially wrong — a composite key with one real
            // part and one placeholder would be just as misleading as a single unset int key.
            if (properties.Any(p => p.IsTemporary)) return null;

            return string.Join("|", properties.Select(p => p.CurrentValue?.ToString() ?? string.Empty));
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
