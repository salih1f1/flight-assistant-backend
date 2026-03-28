using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace FlightAssistant.Infrastructure.Persistance
{
    public class FlightAssistantDbContext : DbContext
    {
        public FlightAssistantDbContext(DbContextOptions<FlightAssistantDbContext> options) : base(options) { }

        // Travelers
        public DbSet<Traveler> Travelers => Set<Traveler>();
        public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

        // Flights
        public DbSet<Flight> Flights => Set<Flight>();
        public DbSet<TravelerFlight> TravelerFlights => Set<TravelerFlight>();
        public DbSet<FlightStatus> FlightStatus => Set<FlightStatus>();
        public DbSet<FlightStatusEvent> FlightStatusEvents => Set<FlightStatusEvent>();

        // Lounges
        public DbSet<Lounge> Lounges => Set<Lounge>();
        public DbSet<LoungeProvider> LoungeProviders => Set<LoungeProvider>();
        public DbSet<LoungeProviderLink> LoungeProviderLinks => Set<LoungeProviderLink>();

        // Loyalty
        public DbSet<LoyaltyProgram> LoyaltyPrograms => Set<LoyaltyProgram>();
        public DbSet<LoyaltyProgramKind> LoyaltyProgramKinds => Set<LoyaltyProgramKind>();
        public DbSet<LoyaltyTier> LoyaltyTiers => Set<LoyaltyTier>();
        public DbSet<TravelerLoyaltyMembership> TravelerLoyaltyMemberships => Set<TravelerLoyaltyMembership>();

        // Reference Data
        public DbSet<Airline> Airlines => Set<Airline>();
        public DbSet<Airport> Airports => Set<Airport>();
        public DbSet<Alliance> Alliances => Set<Alliance>();
        public DbSet<Country> Countries => Set<Country>();

        // Configurations
        public DbSet<SeedVersion> SeedVersions => Set<SeedVersion>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            modelBuilder.Ignore<DomainEvent>();
        }
    }
}
