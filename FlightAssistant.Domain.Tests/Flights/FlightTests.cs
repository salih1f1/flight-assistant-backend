using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.Flights.Events;

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

        [Fact]
        public void ApplyProviderTimes_AResponseWithoutActualTimes_KeepsTheStoredOnes()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(-9), Now.AddHours(-1));
            flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-9), Now.AddMinutes(-50), null, Now);

            flight.ApplyProviderTimes(null, null, null, null, null, Now.AddMinutes(5));

            Assert.Equal(Now.AddHours(-9), flight.ActualDepartureUtc);
            Assert.Equal(Now.AddMinutes(-50), flight.ActualArrivalUtc);
            Assert.Equal(Now, flight.UpdatedUtc); // nothing changed
        }

        [Fact]
        public void ApplyStatusUpdate_AResponseWithoutAnActualArrival_KeepsTheStoredOne()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(-9), Now.AddHours(-1));
            flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-9), Now.AddMinutes(-50), null, Now);

            flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-9), null, null, Now.AddMinutes(5));

            Assert.Equal(Now.AddMinutes(-50), flight.ActualArrivalUtc);
        }

        [Fact]
        public void ApplyProviderTimes_ANewActualTime_ReplacesTheStoredOne()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(-9), Now.AddHours(-1));
            flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-9), Now.AddMinutes(-50), null, Now);

            flight.ApplyProviderTimes(null, null, Now.AddHours(-9), Now.AddMinutes(-45), null, Now.AddMinutes(5));

            Assert.Equal(Now.AddMinutes(-45), flight.ActualArrivalUtc);
            Assert.Equal(Now.AddMinutes(5), flight.UpdatedUtc);
        }

        // ── Push decision (#352) ──

        private static FlightStatusChanged SingleEvent(Flight flight) =>
            Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents));

        [Fact]
        public void ApplyStatusUpdate_AChangedStatus_ShouldNotify()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DEP", null, null, Now, null, null, Now, statusChanged: true);

            var evt = SingleEvent(flight);
            Assert.True(evt.ShouldNotify);
            Assert.Null(evt.DelayMinutes);
        }

        [Fact]
        public void ApplyStatusUpdate_TheSameStatusAgain_DoesNotNotify()
        {
            // A corrected actual departure on DEP records the event without a second push.
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DEP", null, null, Now.AddMinutes(2), null, null, Now, statusChanged: false);

            Assert.False(SingleEvent(flight).ShouldNotify);
        }

        [Fact]
        public void ApplyStatusUpdate_AFirstDelay_NotifiesWithTheMinutesLate_AndRemembersTheEstimate()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: true);

            var evt = SingleEvent(flight);
            Assert.True(evt.ShouldNotify);
            Assert.Equal(40, evt.DelayMinutes);
            Assert.Equal(Now.AddHours(1).AddMinutes(40), flight.DelayNotifiedDepartureUtc);
        }

        [Theory]
        [InlineData(54, false)] // 14 min later than the last push: not enough
        [InlineData(55, true)]  // exactly 15 min later
        [InlineData(30, false)] // the estimate moved earlier
        public void ApplyStatusUpdate_AStillDelayedFlight_NotifiesOnlyWhenTheDelayGrewByTheThreshold(int minutesLate, bool expectNotify)
        {
            var flight = NewFlight();
            var pushedFor = Now.AddHours(1).AddMinutes(40);
            flight.ApplyStatusUpdate("DLY", pushedFor, null, null, null, null, Now, statusChanged: true);
            flight.ClearDomainEvents();

            var estimate = Now.AddHours(1).AddMinutes(minutesLate);
            flight.ApplyStatusUpdate("DLY", estimate, null, null, null, null, Now.AddMinutes(5), statusChanged: false);

            var evt = SingleEvent(flight);
            Assert.Equal(expectNotify, evt.ShouldNotify);
            Assert.Equal(minutesLate, evt.DelayMinutes);
            Assert.Equal(expectNotify ? estimate : pushedFor, flight.DelayNotifiedDepartureUtc);
        }

        [Fact]
        public void ApplyStatusUpdate_SmallSlips_AddUpToAnotherPush()
        {
            // 40 → 50 → 60 min late: neither slip is 15 min, but 60 is 20 min past the last push.
            var flight = NewFlight();
            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: true);
            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(50), null, null, null, null, Now.AddMinutes(5), statusChanged: false);
            flight.ClearDomainEvents();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(60), null, null, null, null, Now.AddMinutes(10), statusChanged: false);

            Assert.True(SingleEvent(flight).ShouldNotify);
        }

        [Fact]
        public void ApplyStatusUpdate_AFlightDelayedBeforeThisShipped_IsPushedOnce()
        {
            // Still DLY (no status change) but no remembered estimate: the next delayed sync pushes once.
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: false);
            Assert.True(SingleEvent(flight).ShouldNotify);
            flight.ClearDomainEvents();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(45), null, null, null, null, Now.AddMinutes(5), statusChanged: false);
            Assert.False(SingleEvent(flight).ShouldNotify);
        }

        [Fact]
        public void ApplyStatusUpdate_ADelayWithoutAnEstimate_NotifiesOnlyOnAStatusChange_WithoutMinutes()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", null, null, null, null, null, Now, statusChanged: false);

            var evt = SingleEvent(flight);
            Assert.False(evt.ShouldNotify);
            Assert.Null(evt.DelayMinutes);
            Assert.Null(flight.DelayNotifiedDepartureUtc);
        }

        [Fact]
        public void ApplyStatusUpdate_ADelayCountsMinutesFromTheReportedSchedule()
        {
            // Stored schedule Now+1h. AeroAPI now schedules Now+1h30 and estimates Now+1h50: 20 min late, not 50.
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(50), null, null, null, null, Now,
                statusChanged: true, reportedScheduledDepartureUtc: Now.AddHours(1).AddMinutes(30));

            Assert.Equal(20, SingleEvent(flight).DelayMinutes);
        }

        [Fact]
        public void ApplyStatusUpdate_AnEstimateNotAfterTheSchedule_HasNoMinutes()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1), null, null, null, null, Now, statusChanged: true);

            Assert.Null(SingleEvent(flight).DelayMinutes);
        }

        [Fact]
        public void ApplyStatusUpdate_ReturningToDelayed_Notifies_EvenWithinTheThreshold()
        {
            // DLY (40) → SCH → DLY (45): back to DLY is a status change, so it pushes.
            var flight = NewFlight();
            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: true);
            flight.ApplyStatusUpdate("SCH", Now.AddHours(1), null, null, null, null, Now.AddMinutes(5), statusChanged: true);
            flight.ClearDomainEvents();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(45), null, null, null, null, Now.AddMinutes(10), statusChanged: true);

            Assert.True(SingleEvent(flight).ShouldNotify);
            Assert.Equal(Now.AddHours(1).AddMinutes(45), flight.DelayNotifiedDepartureUtc);
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
