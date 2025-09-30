using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlightAssistant.Infrastructure.Persistance
{
    public class FlightAssistantDbContextFactory
        : IDesignTimeDbContextFactory<FlightAssistantDbContext>
    {
        public FlightAssistantDbContext CreateDbContext(string[] args)
        {
            // Use a CI env var; any non-empty dummy value is fine at build-time
            var cs = Environment.GetEnvironmentVariable("CONNECTION_STRING")
                     ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default");

            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException(
                    "No CONNECTION_STRING env var found for design-time DbContext.");

            var opts = new DbContextOptionsBuilder<FlightAssistantDbContext>()
                .UseNpgsql(cs)
                .Options;

            return new FlightAssistantDbContext(opts);
        }
    }
}
