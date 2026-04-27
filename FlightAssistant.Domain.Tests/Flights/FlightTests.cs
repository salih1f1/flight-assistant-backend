using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Domain.Tests.Flights
{
    public class FlightTests
    {
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
            flight.ApplyStatusUpdate("DLY", dep, arr, null, null, "B789");

            Assert.Equal(dep, flight.EstimatedDepartureUtc);
            Assert.Equal(arr, flight.EstimatedArrivalUtc);
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
            var evt = FlightStatusEvent.Create(1, 2, DateTime.UtcNow, "AeroDataBox", "ext-123", "A1", "T2");

            Assert.Equal(1, evt.FlightId);
            Assert.Equal(2, evt.StatusId);
            Assert.Equal("AeroDataBox", evt.Source);
            Assert.Equal("ext-123", evt.ExternalId);
            Assert.Equal("A1", evt.Gate);
            Assert.Equal("T2", evt.Terminal);
            Assert.NotEmpty(evt.DedupHash);
        }

        [Fact]
        public void Create_SameInputs_ProduceSameDedupHash()
        {
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var e1 = FlightStatusEvent.Create(1, 2, at, "AeroDataBox", "ext-1");
            var e2 = FlightStatusEvent.Create(1, 2, at, "AeroDataBox", "ext-1");

            Assert.Equal(e1.DedupHash, e2.DedupHash);
        }

        [Fact]
        public void ComputeDedupHash_DifferentStatus_ProducesDifferentHash()
        {
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var h1 = FlightStatusEvent.ComputeDedupHash(1, "DEP", at, "src", null);
            var h2 = FlightStatusEvent.ComputeDedupHash(1, "ARR", at, "src", null);

            Assert.NotEqual(h1, h2);
        }
    }
}
