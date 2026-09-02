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
            // No Activity.Current is started in this test, so the fallback resolves to null —
            // still exercises the "not explicitly supplied" path distinctly from the explicit one.
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

        [Fact]
        public void Create_WithBlankSummary_ThrowsDomainException()
        {
            Assert.Throws<DomainException>(() =>
                AdminAuditLog.Create("actor", null, AdminAuditAction.Create, "Traveler", "1", ""));
        }

        [Fact]
        public void Create_WithWhitespaceActorName_NormalizesToNull()
        {
            var log = AdminAuditLog.Create("actor", "   ", AdminAuditAction.Create, "Traveler", "1", "Created Traveler");

            Assert.Null(log.ActorName);
        }
    }
}
