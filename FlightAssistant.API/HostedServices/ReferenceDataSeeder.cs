using FlightAssistant.Domain.Configuartion.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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

            await SeedAlliances(db, cancellationToken);

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
                AllowTrailingCommas = true
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

        private async Task SeedAlliances(FlightAssistantDbContext db, CancellationToken ct)
        {
            var folder = Path.Combine(AppContext.BaseDirectory, SeedDataFolderPath, "Alliances");

            if (!Directory.Exists(folder)) 
            { 
                log.LogWarning("Seed folder not found: {Folder}", folder); 
                return;
            }

            const string dataset = "Alliances";

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

                List<Alliance> items;
                try
                {
                    items = LoadJsonFromPath<Alliance>(filePath);
                }
                catch (Exception ex)
            {
                    log.LogError(ex, "Failed to read seed file {File}", fileName);
                    continue;
            }

            foreach (var item in items)
            {
                if (!await db.Alliances.AnyAsync(x => x.Code == item.Code, ct))
                {
                    item.SourceType = "seed";
                    item.FirstSeenAt = DateTime.UtcNow;
                    item.LastSeenAt = DateTime.UtcNow;
                    db.Alliances.Add(item);

                        log.LogInformation("Seeded Alliance {Code} - {Name} from {File}", item.Code, item.Name, fileName);
                }
            }

            await db.SaveChangesAsync(ct);
            await MarkAsSeeded(db, dataset, version, ct);
                log.LogInformation("Applied seed {Dataset} v{Version} from {File}", dataset, version, fileName);
            }
        }

    }
}
