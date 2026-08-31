using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Domain.Tests.ReferenceData
{
    public class AirportNormalizationTests
    {
        [Fact]
        public void Create_NormalizesCodesToUpper()
        {
            var airport = Airport.Create("lhr", "egll", "Heathrow", "London", "gb");

            Assert.Equal("LHR", airport.Iata);
            Assert.Equal("EGLL", airport.Icao);
            Assert.Equal("GB", airport.CountryCode);
        }

        [Fact]
        public void Create_TrimsAndUppercasesCodes()
        {
            var airport = Airport.Create("  lhr  ", " egll ", "Heathrow", "London", " gb ");

            Assert.Equal("LHR", airport.Iata);
            Assert.Equal("EGLL", airport.Icao);
            Assert.Equal("GB", airport.CountryCode);
        }

        [Fact]
        public void Create_NullIcao_StaysNull()
        {
            var airport = Airport.Create("lhr", null, "Heathrow", "London", "gb");

            Assert.Null(airport.Icao);
        }

        [Fact]
        public void Update_NormalizesCodesToUpper()
        {
            var airport = Airport.Create("LHR", "EGLL", "Heathrow", "London", "GB");

            airport.Update("cdg", "lfpg", "Charles de Gaulle", "Paris", "fr", null, null, null);

            Assert.Equal("CDG", airport.Iata);
            Assert.Equal("LFPG", airport.Icao);
            Assert.Equal("FR", airport.CountryCode);
        }
    }

    public class AirlineNormalizationTests
    {
        [Fact]
        public void Create_NormalizesCodesToUpper()
        {
            var airline = Airline.Create("ba", "baw", "British Airways", "gb", "oneworld");

            Assert.Equal("BA", airline.Iata);
            Assert.Equal("BAW", airline.Icao);
            Assert.Equal("GB", airline.CountryCode);
            Assert.Equal("ONEWORLD", airline.AllianceCode);
        }

        [Fact]
        public void Create_NullIataAndIcao_StayNull()
        {
            var airline = Airline.Create(null, null, "Some Airline", "gb", null);

            Assert.Null(airline.Iata);
            Assert.Null(airline.Icao);
            Assert.Null(airline.AllianceCode);
        }

        [Fact]
        public void Update_NormalizesCodesToUpper()
        {
            var airline = Airline.Create("BA", "BAW", "British Airways", "GB", null);

            airline.Update("af", "afr", "Air France", "fr", "skyteam");

            Assert.Equal("AF", airline.Iata);
            Assert.Equal("AFR", airline.Icao);
            Assert.Equal("FR", airline.CountryCode);
            Assert.Equal("SKYTEAM", airline.AllianceCode);
        }
    }

    public class CountryNormalizationTests
    {
        [Fact]
        public void Create_NormalizesCodesToUpper()
        {
            var country = Country.Create("gb", "United Kingdom", "gbr");

            Assert.Equal("GB", country.Code);
            Assert.Equal("GBR", country.Alpha3);
        }

        [Fact]
        public void Create_NullAlpha3_StaysNull()
        {
            var country = Country.Create("gb", "United Kingdom", null);

            Assert.Null(country.Alpha3);
        }

        [Fact]
        public void Update_NormalizesAlpha3ToUpper()
        {
            var country = Country.Create("GB", "United Kingdom", "GBR");

            country.Update("United Kingdom", "gbr", null, null, null, null, null, null);

            Assert.Equal("GBR", country.Alpha3);
        }
    }

    public class AllianceNormalizationTests
    {
        [Fact]
        public void Create_NormalizesCodeToUpper()
        {
            var alliance = Alliance.Create("sa", "Star Alliance");

            Assert.Equal("SA", alliance.Code);
        }
    }
}
