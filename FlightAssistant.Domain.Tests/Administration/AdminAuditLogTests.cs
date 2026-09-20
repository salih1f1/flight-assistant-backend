using System.Diagnostics;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.Domain.Tests.Administration
{
    public class AdminAuditLogTests
    {
        [Fact]
        public void Create_WithValidData_SetsAllFields()
        {
            var actionUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

            var log = AdminAuditLog.Create(
                actorId: "actor-oid",
                actorName: "Alice Admin",
                action: AdminAuditAction.Update,
                entityType: "Airport",
                entityId: "42",
                summary: "Updated Airport LHR",
                before: "{\"Iata\":\"LHR\"}",
                after: "{\"Iata\":\"LHR2\"}",
                traceId: "trace-123",
                actionUtc: actionUtc);

            Assert.Equal("actor-oid", log.ActorId);
            Assert.Equal("Alice Admin", log.ActorName);
            Assert.Equal(AdminAuditAction.Update, log.Action);
            Assert.Equal("Airport", log.EntityType);
            Assert.Equal("42", log.EntityId);
            Assert.Equal("Updated Airport LHR", log.Summary);
            Assert.Equal("{\"Iata\":\"LHR\"}", log.Before);
            Assert.Equal("{\"Iata\":\"LHR2\"}", log.After);
            Assert.Equal("trace-123", log.TraceId);
            Assert.Equal(actionUtc, log.ActionUtc);
            Assert.NotEqual(Guid.Empty, log.Id);
        }

        [Fact]
        public void Create_WithoutActionUtc_DefaultsToUtcNow()
        {
            var before = DateTime.UtcNow;
            var log = AdminAuditLog.Create("actor", null, AdminAuditAction.Create, "Traveler", "guid-1", "Created Traveler");
            var after = DateTime.UtcNow;

            Assert.InRange(log.ActionUtc, before, after);
        }

        [Fact]
        public void Create_WithoutTraceId_FallsBackToAmbientActivity()
        {
            // #313 final review, M6: the previous version of this test started no Activity, so it
            // only ever exercised the "no ambient Activity" branch and would still pass with the
            // "?? Activity.Current?.Id" fallback deleted from AdminAuditLog.Create entirely. A real
            // Activity must be started so the assertion actually discriminates the fallback from
            // no fallback. Measured: deleting the fallback from Create (leaving traceId: traceId,
            // dropping the "?? Activity.Current?.Id") makes this test fail with
            // "Assert.Equal() Failure: Values differ - Expected: <activity id> - Actual: null",
            // confirming it now catches the regression it claims to guard against. Restored after
            // measuring.
            using var activity = new Activity("audit-test").Start();

            var log = AdminAuditLog.Create("actor", null, AdminAuditAction.Delete, "Traveler", "guid-1", "Deleted Traveler");

            Assert.Equal(activity.Id, log.TraceId);
        }

        [Fact]
        public void Create_WithoutTraceId_AndNoAmbientActivity_TraceIdIsNull()
        {
            // Distinct from the test above: this pins the other half of the fallback — no ambient
            // Activity means TraceId is genuinely null, not some default/empty-string stand-in.
            Assert.Null(Activity.Current);

            var log = AdminAuditLog.Create("actor", null, AdminAuditAction.Delete, "Traveler", "guid-1", "Deleted Traveler");

            Assert.Null(log.TraceId);
        }

        [Fact]
        public void Create_WithBlankActorId_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() =>
                AdminAuditLog.Create("   ", null, AdminAuditAction.Create, "Traveler", "1", "Created Traveler"));
        }

        [Fact]
        public void Create_WithBlankEntityType_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() =>
                AdminAuditLog.Create("actor", null, AdminAuditAction.Create, "", "1", "Created Traveler"));
        }

        [Fact]
        public void Create_WithBlankEntityId_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() =>
                AdminAuditLog.Create("actor", null, AdminAuditAction.Create, "Traveler", " ", "Created Traveler"));
        }

        // A null EntityId is the legitimate "not known yet" case (a store-generated key on a
        // Create row — see AdminAuditInterceptor.GetEntityId), distinct from blank/whitespace,
        // which is always a caller bug and still rejected above (#313 Task 2 review, F5/M5).
        [Fact]
        public void Create_WithNullEntityId_DoesNotThrow_AndRecordsNull()
        {
            var log = AdminAuditLog.Create(
                "actor", null, AdminAuditAction.Create, "Airport", entityId: null, "Created Airport (pending id)");

            Assert.Null(log.EntityId);
        }

        [Fact]
        public void Create_WithBlankSummary_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() =>
                AdminAuditLog.Create("actor", null, AdminAuditAction.Create, "Traveler", "1", ""));
        }

        /// <summary>
        /// #541. Without these guards an over-long value reached PostgreSQL and failed its column
        /// constraint as an unhandled DbUpdateException — a 500 naming nothing the caller could act
        /// on, and invisible to the API suite, which runs on InMemory and ignores HasMaxLength.
        /// </summary>
        public class LengthGuards
        {
            private static AdminAuditLog Create(
                string? actorId = "actor", string? actorName = null, string? entityId = "1",
                string? entityType = "Traveler", string? summary = "Created Traveler", string? traceId = null) =>
                AdminAuditLog.Create(
                    actorId!, actorName, AdminAuditAction.Create, entityType!, entityId, summary!, traceId: traceId);

            // Identifiers are refused rather than cut: a truncated one still looks real, so an
            // auditor's `WHERE entity_id = '<guid>'` would silently miss the row.
            [Theory]
            [InlineData("actorId")]
            [InlineData("entityType")]
            [InlineData("entityId")]
            [InlineData("summary")]
            [InlineData("traceId")]
            public void Create_WithAnOverLongValue_ThrowsNamingTheColumnAndItsLimit(string column)
            {
                var limit = column switch
                {
                    "actorId" => AdminAuditLog.MaxActorIdLength,
                    "entityType" => AdminAuditLog.MaxEntityTypeLength,
                    "entityId" => AdminAuditLog.MaxEntityIdLength,
                    "summary" => AdminAuditLog.MaxSummaryLength,
                    "traceId" => AdminAuditLog.MaxTraceIdLength,
                    _ => throw new ArgumentOutOfRangeException(nameof(column)),
                };
                var tooLong = new string('x', limit + 1);

                var ex = Assert.Throws<DomainException>(() => column switch
                {
                    "actorId" => Create(actorId: tooLong),
                    "entityType" => Create(entityType: tooLong),
                    "entityId" => Create(entityId: tooLong),
                    "summary" => Create(summary: tooLong),
                    "traceId" => Create(traceId: tooLong),
                    _ => throw new ArgumentOutOfRangeException(nameof(column)),
                });

                Assert.Contains(column, ex.Message);
                Assert.Contains(limit.ToString(), ex.Message);
            }

            [Fact]
            public void Create_WithAValueExactlyAtItsLimit_IsAccepted()
            {
                var log = Create(entityId: new string('x', AdminAuditLog.MaxEntityIdLength));

                Assert.Equal(AdminAuditLog.MaxEntityIdLength, log.EntityId!.Length);
            }

            /// <summary>
            /// The one column cut rather than refused, and the only one an operator can realistically
            /// overflow — it comes from a display-name claim. Refusing it would stop an admin whose
            /// name is over 200 characters performing any audited write, and since #531/#540 stage the
            /// audit row in the same transaction as the change, that means their change too.
            /// </summary>
            [Fact]
            public void Create_WithAnOverLongActorName_TruncatesItRatherThanRefusingTheWrite()
            {
                var log = Create(actorName: new string('n', AdminAuditLog.MaxActorNameLength + 50));

                Assert.Equal(AdminAuditLog.MaxActorNameLength, log.ActorName!.Length);
            }

            [Fact]
            public void Create_WhenItTruncatesAnActorName_MarksThatItWasCut()
            {
                var log = Create(actorName: new string('n', AdminAuditLog.MaxActorNameLength + 50));

                Assert.EndsWith("…", log.ActorName);
            }

            [Fact]
            public void Create_WithAnActorNameThatFits_LeavesItUnmarked()
            {
                var log = Create(actorName: "Ada Lovelace");

                Assert.Equal("Ada Lovelace", log.ActorName);
            }
        }

        [Fact]
        public void Create_WithWhitespaceActorName_NormalizesToNull()
        {
            var log = AdminAuditLog.Create("actor", "   ", AdminAuditAction.Create, "Traveler", "1", "Created Traveler");

            Assert.Null(log.ActorName);
        }
    }
}
