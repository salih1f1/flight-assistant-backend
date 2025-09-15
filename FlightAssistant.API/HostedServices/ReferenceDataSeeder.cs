using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Configuartion.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography.Xml;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;   

namespace FlightAssistant.API.HostedServices
{
    public class ReferenceDataSeeder : IHostedService
    {
        private readonly IServiceProvider sp;
        private readonly ILogger<ReferenceDataSeeder> log;

        private const string SeedDataFolderPath = "SeedData";
        private static readonly Regex VersionFromFilename = new Regex(@"\.v(?<v>\d+)\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ReferenceDataSeeder(
            IServiceProvider sp, 
            ILogger<ReferenceDataSeeder> log)
        {
            this.sp = sp;
            this.log = log;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            log.LogInformation("Reference data seeding started.");

            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

            await SeedData<Alliance>(db, cancellationToken);
            await SeedData<Country>(db, cancellationToken);
            await SeedData<Airport>(db, cancellationToken);
            await SeedData<Airline>(db, cancellationToken);

            log.LogInformation("Reference data seeding completed.");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task<bool> AlreadySeeded(FlightAssistantDbContext db, string dataset, int version, CancellationToken ct)
        {
            return await db.SeedVersions.AnyAsync(x => x.Dataset == dataset && x.Version == version, ct);
        }

        private static List<T> LoadJsonFromPath<T>(string absolutePath)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            var fileText = File.ReadAllText(absolutePath);
            return JsonSerializer.Deserialize<List<T>>(fileText, options)!;
        }

        private async Task MarkAsSeeded(FlightAssistantDbContext db, string dataset, int version, CancellationToken ct)
        {
            db.SeedVersions.Add(new SeedVersion
            {
                Dataset = dataset,
                Version = version,
                AppliedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        private static bool TryParseVersion(string fileName, out int version)
        {
            var m = VersionFromFilename.Match(fileName);
            if (m.Success && int.TryParse(m.Groups["v"].Value, out version)) return true;
            version = 0;
            return false;
        }

        private async Task SeedData<T>(FlightAssistantDbContext db, CancellationToken ct) 
            where T : class, ISeadableEntity
        {
            var dataset = typeof(T).Name;

            var folder = Path.Combine(AppContext.BaseDirectory, SeedDataFolderPath, dataset);

            if (!Directory.Exists(folder)) 
            { 
                log.LogWarning("Seed folder not found: {Folder}", folder); 
                return;
            }

            foreach (var filePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(filePath);

                if (!TryParseVersion(fileName, out var version))
                {
                    log.LogWarning("Skipping seed file without version suffix: {File}", fileName);
                    continue;
            }

                if (await AlreadySeeded(db, dataset, version, ct))
                {
                    log.LogInformation("Skipping {Dataset} v{Version} from {File}, already applied", dataset, version, fileName);
                    continue;
                }

                List<T> items;
                try
                {
                    items = LoadJsonFromPath<T>(filePath);
                }
                catch (Exception ex)
            {
                    log.LogError(ex, "Failed to read seed file {File}", fileName);
                    continue;
            }

            foreach (var item in items)
            {
                    switch (item)
                    {
                        case Alliance alliance:
                            await PopulateAlliancesAsync(alliance, db, ct);
                            break;

                        case Country country:
                            await PopulateCountriesAsync(country, db, ct);
                            break;

                        case Airport airport:
                            await PopulateAirportsAsync(airport, db, ct);
                            break;

                        case Airline airline:
                            await PopulateAirlinesAsync(airline, db, ct);
                            break;

                        default:
                            log.LogError("Seeding for type {0} is not implemented", dataset);
                            return;
                    }
            }

            await db.SaveChangesAsync(ct);
            await MarkAsSeeded(db, dataset, version, ct);
                log.LogInformation("Applied seed {Dataset} v{Version} from {File}", dataset, version, fileName);
            }
        }

        private async Task PopulateAlliancesAsync(
            Alliance alliance,
            FlightAssistantDbContext db,
            CancellationToken ct)
        {
            if (!await db.Alliances.AnyAsync(x => x.Code == alliance.Code, ct))
            {
                alliance.SourceType = "seed";
                alliance.FirstSeenAt = DateTime.UtcNow;
                alliance.LastSeenAt = DateTime.UtcNow;
                await db.Alliances.AddAsync(alliance);
            }
        }

        private async Task PopulateCountriesAsync(
            Country country,
            FlightAssistantDbContext db,
            CancellationToken ct)
        {
            if (!await db.Countries.AnyAsync(x => (x.Code == country.Code || x.Alpha3 == country.Alpha3), ct))
            {
                country.SourceType = "seed";
                country.FirstSeenAt = DateTime.UtcNow;
                country.LastSeenAt = DateTime.UtcNow;
                await db.Countries.AddAsync(country);
            }
        }

        private async Task PopulateAirportsAsync(
            Airport airport,
            FlightAssistantDbContext db,
            CancellationToken ct)
        {
            if (!await db.Airports.AnyAsync(x => (x.Iata == airport.Iata || x.Icao == airport.Icao), ct))
            {
                airport.SourceType = "seed";
                airport.FirstSeenAt = DateTime.UtcNow;
                airport.LastSeenAt = DateTime.UtcNow;
                await db.Airports.AddAsync(airport);
            }
        }

        private async Task PopulateAirlinesAsync(
            Airline airline,
            FlightAssistantDbContext db,
            CancellationToken ct)
        {
            if (!await db.Airlines.AnyAsync(x => (x.Iata == airline.Iata || x.Icao == airline.Icao), ct))
            {
                if (airline.Iata?.Length > 3 || airline.Icao?.Length > 4)
                {
                    throw new Exception();
                }

                airline.SourceType = "seed";
                airline.FirstSeenAt = DateTime.UtcNow;
                airline.LastSeenAt = DateTime.UtcNow;
                await db.Airlines.AddAsync(airline);
            }
        }
    }
}
