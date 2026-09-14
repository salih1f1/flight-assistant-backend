using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Domain.Tests.Loyalty
{
    /// <summary>
    /// <see cref="LoyaltyProgram.CreatedUtc"/>: when an admin or an import added the program (#334).
    /// Unknown (null) for rows that predate the column and for seeded rows, so the dashboard never
    /// counts them as new.
    /// </summary>
    public class LoyaltyProgramCreationTimeTests
    {
        [Fact]
        public void Create_SetsCreatedUtcToNow()
        {
            var before = DateTime.UtcNow;

            var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1);

            Assert.NotNull(program.CreatedUtc);
            Assert.InRange(program.CreatedUtc!.Value, before, DateTime.UtcNow);
            Assert.Equal(DateTimeKind.Utc, program.CreatedUtc.Value.Kind);
        }

        [Fact]
        public void Update_KeepsCreatedUtc()
        {
            var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1);
            var created = program.CreatedUtc;

            program.Update("BA", "The Club", 2, airlineId: null, allianceCode: "OW");

            Assert.Equal(created, program.CreatedUtc);
        }

        [Fact]
        public void CreateSeeded_LeavesCreatedUtcUnknown_AndSetsEverythingElse()
        {
            var program = LoyaltyProgram.CreateSeeded("BA", "British Airways Executive Club", 2, airlineId: 7, allianceCode: "OW");

            Assert.Null(program.CreatedUtc);
            Assert.Equal("BA", program.Code);
            Assert.Equal("British Airways Executive Club", program.Name);
            Assert.Equal(2, program.KindId);
            Assert.Equal(7, program.AirlineId);
            Assert.Equal("OW", program.AllianceCode);
        }
    }
}
