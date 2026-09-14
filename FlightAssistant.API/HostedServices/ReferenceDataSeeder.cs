using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
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
        private const string SeedSourceType = "seed";
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

            try
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

                await SeedAlliancesAsync(db, cancellationToken);
                await SeedCountriesAsync(db, cancellationToken);
                await SeedAirportsAsync(db, cancellationToken);
                await SeedAirlinesAsync(db, cancellationToken);
                await SeedLoyaltyProgramKindsAsync(db, cancellationToken);
                await SeedLoyaltyProgramsAsync(db, cancellationToken);
                await SeedLoungeProvidersAsync(db, cancellationToken);
                await SeedLoungesAsync(db, cancellationToken);
                await SeedLoungeProviderLinksAsync(db, cancellationToken);

                log.LogInformation("Reference data seeding completed.");
            }
            catch (Exception ex)
            {
                // Seeding failure must not crash the host — the app can still serve
                // requests and seeding will succeed on the next deploy once the DB
                // is reachable.
                log.LogError(ex, "Reference data seeding failed. The application will continue without fresh seed data.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // -----------------------------------------------------------------------
        // Alliances
        // -----------------------------------------------------------------------

        private async Task SeedAlliancesAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "Alliance";
            await SeedCustomAsync<AllianceSeedDto>(db, dataset, ct, async (dto, d, c) =>
            {
                await PopulateAlliancesAsync(Alliance.Create(dto.Code, dto.Name), d, c);
            });
        }

        // -----------------------------------------------------------------------
        // Countries
        // -----------------------------------------------------------------------

        private async Task SeedCountriesAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "Country";
            await SeedCustomAsync<CountrySeedDto>(db, dataset, ct, async (dto, d, c) =>
            {
                await PopulateCountriesAsync(
                    Country.Create(dto.Code, dto.Name, dto.Alpha3, dto.NumericCode, dto.Region, dto.Subregion, dto.IsEU, dto.IsSchengen, dto.IsEEA),
                    d, c);
            });
        }

        // -----------------------------------------------------------------------
        // Airports
        // -----------------------------------------------------------------------

        private async Task SeedAirportsAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "Airport";
            await SeedCustomAsync<AirportSeedDto>(db, dataset, ct, async (dto, d, c) =>
            {
                await PopulateAirportsAsync(
                    Airport.Create(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode, dto.Latitude, dto.Longitude, dto.ElevationFt),
                    d, c);
            });
        }

        // -----------------------------------------------------------------------
        // Airlines
        // -----------------------------------------------------------------------

        private async Task SeedAirlinesAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "Airline";
            await SeedCustomAsync<AirlineSeedDto>(db, dataset, ct, async (dto, d, c) =>
            {
                await PopulateAirlinesAsync(
                    Airline.Create(dto.Iata, dto.Icao, dto.Name, dto.CountryCode, dto.AllianceCode),
                    d, c);
            });
        }

        // -----------------------------------------------------------------------
        // Loyalty Program Kinds
        // -----------------------------------------------------------------------

        private async Task SeedLoyaltyProgramKindsAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "LoyaltyProgramKind";
            await SeedCustomAsync<LoyaltyProgramKindDto>(db, dataset, ct, async (dto, d, c) =>
            {
                if (!await d.Set<LoyaltyProgramKind>().AnyAsync(k => k.Code == dto.Code, c))
                    await d.Set<LoyaltyProgramKind>().AddAsync(LoyaltyProgramKind.Create(dto.Code, dto.Name, dto.Description), c);
            });
        }

        // -----------------------------------------------------------------------
        // Loyalty Programs + Tiers
        // -----------------------------------------------------------------------

        private async Task SeedLoyaltyProgramsAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "LoyaltyProgram";
            await SeedCustomAsync<LoyaltyProgramDto>(db, dataset, ct, async (dto, d, c) =>
            {
                if (await d.LoyaltyPrograms.AnyAsync(p => p.Code == dto.Code, c))
                    return;

                var kindId = await d.Set<LoyaltyProgramKind>()
                    .Where(k => k.Code == dto.KindCode)
                    .Select(k => (int?)k.Id)
                    .FirstOrDefaultAsync(c);

                if (kindId is null)
                {
                    log.LogWarning("Skipping loyalty program {Code}: kind '{Kind}' not found", dto.Code, dto.KindCode);
                    return;
                }

                int? airlineId = null;
                if (!string.IsNullOrWhiteSpace(dto.AirlineIata))
                {
                    airlineId = await d.Airlines
                        .Where(a => a.Iata == dto.AirlineIata)
                        .Select(a => (int?)a.Id)
                        .FirstOrDefaultAsync(c);

                    if (airlineId is null)
                        log.LogWarning("Loyalty program {Code}: airline IATA '{Iata}' not found; seeding without airline link", dto.Code, dto.AirlineIata);
                }

                // Seeded, not added by anyone: no creation time, so a fresh environment's seed never reads as new (#334).
                var program = LoyaltyProgram.CreateSeeded(dto.Code, dto.Name, kindId.Value, airlineId, dto.AllianceCode);
                await d.LoyaltyPrograms.AddAsync(program, c);
                await d.SaveChangesAsync(c);

                foreach (var tierDto in dto.Tiers)
                {
                    if (!await d.Set<LoyaltyTier>().AnyAsync(t => t.ProgramId == program.Id && t.Code == tierDto.Code, c))
                    {
                        var tier = LoyaltyTier.Create(program.Id, tierDto.Code, tierDto.Name, tierDto.Rank,
                            tierDto.AllianceCode, tierDto.AllianceTierCode);
                        await d.Set<LoyaltyTier>().AddAsync(tier, c);
                    }
                }
            });
        }

        // -----------------------------------------------------------------------
        // Lounge Providers
        // -----------------------------------------------------------------------

        private async Task SeedLoungeProvidersAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "LoungeProvider";
            await SeedCustomAsync<LoungeProviderSeedDto>(db, dataset, ct, async (dto, d, c) =>
            {
                if (await d.Set<LoungeProvider>().AnyAsync(p => p.Code == dto.Code, c))
                    return;

                if (!Enum.TryParse<LoungeProviderKind>(dto.Kind, ignoreCase: true, out var kind))
                {
                    log.LogWarning("Skipping lounge provider '{Code}': unknown kind '{Kind}'", dto.Code, dto.Kind);
                    return;
                }

                await d.Set<LoungeProvider>().AddAsync(LoungeProvider.Create(dto.Code, dto.Name, kind, dto.Description), c);
            });
        }

        // -----------------------------------------------------------------------
        // Lounges
        // -----------------------------------------------------------------------

        private async Task SeedLoungesAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "Lounge";
            await SeedCustomAsync<LoungeSeedDto>(db, dataset, ct, async (dto, d, c) =>
            {
                var airportId = await d.Airports
                    .Where(a => a.Iata == dto.AirportIata)
                    .Select(a => (int?)a.Id)
                    .FirstOrDefaultAsync(c);

                if (airportId is null)
                {
                    log.LogWarning("Skipping lounge '{Name}': airport IATA '{Iata}' not found", dto.Name, dto.AirportIata);
                    return;
                }

                var existingLounge = await d.Lounges
                    .Where(l => l.AirportId == airportId && l.Name == dto.Name && l.Terminal == dto.Terminal)
                    .Select(l => (int?)l.Id)
                    .FirstOrDefaultAsync(c);

                int loungeId;
                if (existingLounge is null)
                {
                    // Seeded, not added by anyone: no creation time (#334).
                    var lounge = Lounge.CreateSeeded(
                        airportId.Value, dto.Name, dto.Terminal, dto.LocationHint,
                        dto.Hours, dto.IsAirside, dto.IsDomesticOnly, dto.IsInternationalOnly, dto.Amenities);
                    await d.Lounges.AddAsync(lounge, c);
                    await d.SaveChangesAsync(c);
                    loungeId = lounge.Id;
                }
                else
                {
                    loungeId = existingLounge.Value;
                }

                if (dto.Providers is { Count: > 0 })
                {
                    foreach (var linkDto in dto.Providers)
                    {
                        if (!Enum.TryParse<LoungeProviderRole>(linkDto.Role, ignoreCase: true, out var role))
                        {
                            log.LogWarning("Skipping provider link for lounge '{Name}': unknown role '{Role}'", dto.Name, linkDto.Role);
                            continue;
                        }

                        var providerId = await d.Set<LoungeProvider>()
                            .Where(p => p.Code == linkDto.ProviderCode)
                            .Select(p => (int?)p.Id)
                            .FirstOrDefaultAsync(c);

                        if (providerId is null)
                        {
                            log.LogWarning("Skipping provider link for lounge '{Name}': provider '{Code}' not found", dto.Name, linkDto.ProviderCode);
                            continue;
                        }

                        var linkExists = await d.Set<LoungeProviderLink>()
                            .AnyAsync(l => l.LoungeId == loungeId && l.ProviderId == providerId && l.Role == role, c);

                        if (!linkExists)
                        {
                            await d.Set<LoungeProviderLink>().AddAsync(
                                LoungeProviderLink.Create(loungeId, providerId.Value, role, linkDto.Notes, linkDto.MinAllianceTierCode), c);
                        }
                    }
                }
            });
        }

        // -----------------------------------------------------------------------
        // Lounge Provider Links (flat seed — links existing lounges to providers)
        // -----------------------------------------------------------------------

        private async Task SeedLoungeProviderLinksAsync(FlightAssistantDbContext db, CancellationToken ct)
        {
            const string dataset = "LoungeProviderLink";
            await SeedCustomAsync<FlatLoungeProviderLinkDto>(db, dataset, ct, async (dto, d, c) =>
            {
                if (!Enum.TryParse<LoungeProviderRole>(dto.Role, ignoreCase: true, out var role))
                {
                    log.LogWarning("Skipping provider link: unknown role '{Role}'", dto.Role);
                    return;
                }

                var airportId = await d.Airports
                    .Where(a => a.Iata == dto.AirportIata)
                    .Select(a => (int?)a.Id)
                    .FirstOrDefaultAsync(c);

                if (airportId is null)
                {
                    log.LogWarning("Skipping provider link for lounge '{Name}': airport '{Iata}' not found", dto.LoungeName, dto.AirportIata);
                    return;
                }

                var loungeId = await d.Lounges
                    .Where(l => l.AirportId == airportId && l.Name == dto.LoungeName && l.Terminal == dto.Terminal)
                    .Select(l => (int?)l.Id)
                    .FirstOrDefaultAsync(c);

                if (loungeId is null)
                {
                    log.LogWarning("Skipping provider link: lounge '{Name}' at {Iata}/{Terminal} not found", dto.LoungeName, dto.AirportIata, dto.Terminal);
                    return;
                }

                var providerId = await d.Set<LoungeProvider>()
                    .Where(p => p.Code == dto.ProviderCode)
                    .Select(p => (int?)p.Id)
                    .FirstOrDefaultAsync(c);

                if (providerId is null)
                {
                    log.LogWarning("Skipping provider link for lounge '{Name}': provider '{Code}' not found", dto.LoungeName, dto.ProviderCode);
                    return;
                }

                var exists = await d.Set<LoungeProviderLink>()
                    .AnyAsync(l => l.LoungeId == loungeId && l.ProviderId == providerId && l.Role == role, c);

                if (!exists)
                {
                    await d.Set<LoungeProviderLink>().AddAsync(
                        LoungeProviderLink.Create(loungeId.Value, providerId.Value, role, dto.Notes, dto.MinAllianceTierCode), c);
                }
            });
        }

        // -----------------------------------------------------------------------
        // Generic helpers
        // -----------------------------------------------------------------------

        private async Task SeedCustomAsync<TDto>(
            FlightAssistantDbContext db,
            string dataset,
            CancellationToken ct,
            Func<TDto, FlightAssistantDbContext, CancellationToken, Task> populate)
        {
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

                List<TDto> items;
                try
                {
                    items = LoadJsonFromPath<TDto>(filePath);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to read seed file {File}", fileName);
                    continue;
                }

                foreach (var item in items)
                {
                    try
                    {
                        await populate(item, db, ct);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Error seeding item in {Dataset}", dataset);
                    }
                }

                await db.SaveChangesAsync(ct);
                await MarkAsSeeded(db, dataset, version, ct);
                log.LogInformation("Applied seed {Dataset} v{Version} from {File}", dataset, version, fileName);
            }
        }

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

        private async Task PopulateAlliancesAsync(
            Alliance alliance,
            FlightAssistantDbContext db,
            CancellationToken ct)
        {
            if (!await db.Alliances.AnyAsync(x => x.Code == alliance.Code, ct))
            {
                alliance.SourceType = SeedSourceType;
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
                country.SourceType = SeedSourceType;
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
                airport.SourceType = SeedSourceType;
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
                    throw new Exception(
                        $"Invalid airline code length: IATA ('{airline.Iata}') must be at most 3 characters, ICAO ('{airline.Icao}') must be at most 4 characters.");
                }

                airline.SourceType = SeedSourceType;
                await db.Airlines.AddAsync(airline);
            }
        }

        // -----------------------------------------------------------------------
        // JSON DTOs (used only for deserialization, not persisted directly)
        // -----------------------------------------------------------------------

        private record AllianceSeedDto(string Code, string Name);

        private record CountrySeedDto(
            string Code,
            string Name,
            string? Alpha3 = null,
            int? NumericCode = null,
            string? Region = null,
            string? Subregion = null,
            bool? IsEU = null,
            bool? IsSchengen = null,
            bool? IsEEA = null);

        private record AirportSeedDto(
            string Iata,
            string? Icao,
            string Name,
            string City,
            string CountryCode,
            double? Latitude = null,
            double? Longitude = null,
            int? ElevationFt = null);

        private record AirlineSeedDto(
            string? Iata,
            string? Icao,
            string Name,
            string CountryCode,
            string? AllianceCode = null);

        private record LoyaltyProgramKindDto(string Code, string Name, string? Description);

        private record LoyaltyTierDto(string Code, string Name, int Rank,
            string? AllianceCode = null, string? AllianceTierCode = null);

        private record LoyaltyProgramDto(
            string Code,
            string Name,
            string KindCode,
            string? AirlineIata,
            string? AllianceCode,
            List<LoyaltyTierDto> Tiers);

        private record LoungeProviderSeedDto(string Code, string Name, string Kind, string? Description = null);

        private record LoungeProviderLinkSeedDto(
            string ProviderCode,
            string Role,
            string? MinAllianceTierCode = null,
            string? Notes = null);

        private record FlatLoungeProviderLinkDto(
            string AirportIata,
            string LoungeName,
            string? Terminal,
            string ProviderCode,
            string Role,
            string? MinAllianceTierCode = null,
            string? Notes = null);

        private record LoungeSeedDto(
            string AirportIata,
            string Name,
            string? Terminal,
            string? LocationHint,
            string? Hours,
            bool IsAirside,
            bool IsDomesticOnly,
            bool IsInternationalOnly,
            string? Amenities,
            List<LoungeProviderLinkSeedDto>? Providers = null);
    }
}
