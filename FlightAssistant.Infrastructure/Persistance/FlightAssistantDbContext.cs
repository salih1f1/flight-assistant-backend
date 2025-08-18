using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace FlightAssistant.Infrastructure.Persistance
{
    public class FlightAssistantDbContext : DbContext
    {
        public FlightAssistantDbContext(DbContextOptions<FlightAssistantDbContext> options) : base(options) { }

        //// Travelers
        //public DbSet<Traveler> Travelers => Set<Traveler>();
        //public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

        public DbSet<Country> Countries => Set<Country>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            modelBuilder.Ignore<DomainEvent>();
        }

    }
}
