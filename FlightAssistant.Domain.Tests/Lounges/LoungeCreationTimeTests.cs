using FlightAssistant.Domain.Lounges.Entities;

namespace FlightAssistant.Domain.Tests.Lounges
{
    /// <summary>
    /// <see cref="Lounge.CreatedUtc"/>: when an admin or an import added the lounge (#334). Unknown
    /// (null) for rows that predate the column and for seeded rows, so the dashboard never counts
    /// them as new.
    /// </summary>
    public class LoungeCreationTimeTests
    {
        [Fact]
        public void Create_SetsCreatedUtcToNow()
        {
            var before = DateTime.UtcNow;

            var lounge = Lounge.Create(1, "Test Lounge");

            Assert.NotNull(lounge.CreatedUtc);
            Assert.InRange(lounge.CreatedUtc!.Value, before, DateTime.UtcNow);
            Assert.Equal(DateTimeKind.Utc, lounge.CreatedUtc.Value.Kind);
        }

        [Fact]
        public void Update_KeepsCreatedUtc()
        {
            var lounge = Lounge.Create(1, "Test Lounge");
            var created = lounge.CreatedUtc;

            lounge.Update("Renamed Lounge", "5", null, null, true, false, false, null);

            Assert.Equal(created, lounge.CreatedUtc);
        }

        [Fact]
        public void CreateSeeded_LeavesCreatedUtcUnknown_AndSetsEverythingElse()
        {
            var lounge = Lounge.CreateSeeded(7, "Seeded Lounge", terminal: "5", hours: "05:00-22:00", isAirside: false);

            Assert.Null(lounge.CreatedUtc);
            Assert.Equal(7, lounge.AirportId);
            Assert.Equal("Seeded Lounge", lounge.Name);
            Assert.Equal("5", lounge.Terminal);
            Assert.Equal("05:00-22:00", lounge.Hours);
            Assert.False(lounge.IsAirside);
        }
    }
}
