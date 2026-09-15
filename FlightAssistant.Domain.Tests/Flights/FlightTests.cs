using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;

namespace FlightAssistant.Domain.Tests.Flights
{
    public class FlightTests
    {
        private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

        private static Flight NewFlight() => Flight.Create(1, "TK1028", 1, 2, new DateOnly(2026, 9, 14),
            Now.AddHours(1), Now.AddHours(4));

        [Fact]
        public void Create_NormalizesFlightNumber()
        {
            var flight = Flight.Create(1, "ba123", 1, 2, DateOnly.FromDateTime(DateTime.Today),
                DateTime.UtcNow, DateTime.UtcNow.AddHours(2));

            Assert.Equal("BA123", flight.FlightNumber);
        }

        [Fact]
        public void Create_WithEmptyFlightNumber_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                Flight.Create(1, "", 1, 2, DateOnly.FromDateTime(DateTime.Today), DateTime.UtcNow, DateTime.UtcNow));
        }

        [Fact]
        public void ApplyStatusUpdate_UpdatesTimestamps()
        {
            var flight = Flight.Create(1, "BA123", 1, 2, DateOnly.FromDateTime(DateTime.Today),
                DateTime.UtcNow, DateTime.UtcNow.AddHours(2));

            var dep = DateTime.UtcNow;
            var arr = DateTime.UtcNow.AddHours(2);
            flight.ApplyStatusUpdate("DLY", dep, arr, null, null, "B789", DateTime.UtcNow);

            Assert.Equal(dep, flight.EstimatedDepartureUtc);
            Assert.Equal(arr, flight.EstimatedArrivalUtc);
            Assert.Equal("B789", flight.EquipmentAircraftIcao);
        }

        [Fact]
        public void ApplyStatusUpdate_Cancelled_SetsCancelledAtUtc()
        {
            var flight = NewFlight();
            flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
            Assert.Equal(Now, flight.CancelledAtUtc);
        }

        [Fact]
        public void ApplyStatusUpdate_CancelledAgain_KeepsTheFirstCancellationTime()
        {
            // A later CAN update must not push the Stopped rule's 30-minute clock forward.
            var flight = NewFlight();
            flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
            flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now.AddMinutes(20));
            Assert.Equal(Now, flight.CancelledAtUtc);
        }

        [Fact]
        public void ApplyStatusUpdate_OtherCodeAfterCancelled_ClearsCancelledAtUtc()
        {
            var flight = NewFlight();
            flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
            flight.ApplyStatusUpdate("SCH", null, null, null, null, null, Now.AddMinutes(5));
            Assert.Null(flight.CancelledAtUtc);
        }

        [Fact]
        public void MarkSynced_SetsLastSyncedUtc()
        {
            var flight = NewFlight();
            flight.MarkSynced(Now);
            Assert.Equal(Now, flight.LastSyncedUtc);
        }

        [Fact]
        public void ApplySyncPlan_SetsModeAndNextDue()
        {
            var flight = NewFlight();
            flight.ApplySyncPlan(FlightSyncMode.Live, Now.AddMinutes(10));
            Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
            Assert.Equal(Now.AddMinutes(10), flight.NextSyncDueUtc);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(FlightSyncMode.Stopped)]
        public void ApplySyncPlan_NotSyncedModeWithANextDueTime_Throws(FlightSyncMode? mode)
        {
            var flight = NewFlight();
            Assert.Throws<ArgumentException>(() => flight.ApplySyncPlan(mode, Now));
        }

        [Fact]
        public void ApplyProviderTimes_UpdatesTheTimes_RaisesNoEvent_AndLeavesCancelledAtUtc()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(8));
            flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
            flight.ClearDomainEvents();

            flight.ApplyProviderTimes(Now.AddHours(2), Now.AddHours(9), null, null, "A321", Now.AddMinutes(5));

            Assert.Equal(Now.AddHours(2), flight.EstimatedDepartureUtc);
            Assert.Equal(Now.AddHours(9), flight.EstimatedArrivalUtc);
            Assert.Equal("A321", flight.EquipmentAircraftIcao);
            Assert.Equal(Now, flight.CancelledAtUtc);
            Assert.Equal(Now.AddMinutes(5), flight.UpdatedUtc);
            Assert.Empty(flight.DomainEvents);
        }

        [Fact]
        public void ApplyProviderTimes_NothingChanged_LeavesUpdatedUtc_AndAMissingAircraftTypeKeepsTheStoredOne()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(8), "B789");
            flight.ApplyStatusUpdate("DEP", Now.AddHours(1), Now.AddHours(8), Now.AddHours(1), null, "B789", Now);

            flight.ApplyProviderTimes(Now.AddHours(1), Now.AddHours(8), Now.AddHours(1), null, null, Now.AddMinutes(5));

            Assert.Equal(Now, flight.UpdatedUtc);
            Assert.Equal("B789", flight.EquipmentAircraftIcao);
        }
    }

    public class TravelerFlightTests
    {
        [Fact]
        public void Create_SetsNotifyTrue()
        {
            var tf = TravelerFlight.Create(Guid.NewGuid(), 42);
            Assert.True(tf.Notify);
        }

        [Fact]
        public void SetNotify_ChangesFlag()
        {
            var tf = TravelerFlight.Create(Guid.NewGuid(), 42);
            tf.SetNotify(false);
            Assert.False(tf.Notify);
        }
    }

    public class FlightStatusEventTests
    {
        [Fact]
        public void Create_SetsFields()
        {
            var evt = FlightStatusEvent.Create(1, 2, DateTime.UtcNow, "AeroAPI", "ext-123", "A1", "T2");

            Assert.Equal(1, evt.FlightId);
            Assert.Equal(2, evt.StatusId);
            Assert.Equal("AeroAPI", evt.Source);
            Assert.Equal("ext-123", evt.ExternalId);
            Assert.Equal("A1", evt.Gate);
            Assert.Equal("T2", evt.Terminal);
            Assert.NotEmpty(evt.DedupHash);
        }

        [Fact]
        public void Create_SameInputs_ProduceSameDedupHash()
        {
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var e1 = FlightStatusEvent.Create(1, 2, at, "AeroAPI", "ext-1");
            var e2 = FlightStatusEvent.Create(1, 2, at, "AeroAPI", "ext-1");

            Assert.Equal(e1.DedupHash, e2.DedupHash);
        }

        [Fact]
        public void ComputeDedupHash_DifferentStatus_ProducesDifferentHash()
        {
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var h1 = FlightStatusEvent.ComputeDedupHash(1, 2, at, "src", null);
            var h2 = FlightStatusEvent.ComputeDedupHash(1, 3, at, "src", null);

            Assert.NotEqual(h1, h2);
        }

        [Fact]
        public void ComputeDedupHash_MatchesStoredHashFromCreate()
        {
            // Regression for #232: the pre-insert dedup check and the stored hash must be
            // computed over IDENTICAL inputs, otherwise dedup never matches.
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var precheckHash = FlightStatusEvent.ComputeDedupHash(1, 2, at, "AeroAPI", "ext-1");
            var evt = FlightStatusEvent.Create(1, 2, at, "AeroAPI", "ext-1");

            Assert.Equal(evt.DedupHash, precheckHash);
        }

        [Fact]
        public void ComputeDedupHash_FormulaIsUnchanged()
        {
            // Every stored hash depends on this formula (#351). Changing it would re-record, and push,
            // every tracked flight's latest status on its next sync.
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

            Assert.Equal("47E102A0F36D99F2", FlightStatusEvent.ComputeDedupHash(1, 2, at, "AeroAPI", "ext-1"));
        }

        [Fact]
        public void CreateDetailChange_HashDiffersFromTheStatusHash_AndPerPreviousEvent()
        {
            // A gate that changes back must still get a new hash under the unique (FlightId, Source, DedupHash) index.
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var status = FlightStatusEvent.Create(1, 2, at, "AeroAPI", "ext-1", "A1", "T5");
            var toB7 = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 10, "B7", "T5", null);
            var backToA1 = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 11, "A1", "T5", null);
            var toB7Again = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 12, "B7", "T5", null);

            Assert.Equal(4, new[] { status.DedupHash, toB7.DedupHash, backToA1.DedupHash, toB7Again.DedupHash }.Distinct().Count());
            Assert.Equal("B7", toB7.Gate);
            Assert.Equal("T5", toB7.Terminal);
            Assert.Equal(2, toB7.StatusId);
            Assert.Equal(at, toB7.EffectiveAtUtc);
        }

        [Fact]
        public void CreateDetailChange_StoresTheHashComputeDetailChangeHashGives()
        {
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var evt = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 10, "B7", "T5", "7");

            Assert.Equal(FlightStatusEvent.ComputeDetailChangeHash(1, 2, at, "AeroAPI", "ext-1", 10, "B7", "T5", "7"), evt.DedupHash);
            Assert.Equal("7", evt.BaggageClaim);
        }
    }
}
