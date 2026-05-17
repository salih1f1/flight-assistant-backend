using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AirportImportDto(
    string Iata, string? Icao, string Name, string City, string CountryCode,
    double? Latitude, double? Longitude, int? ElevationFt, string? Timezone);

public record ImportAirportsCommand(IReadOnlyList<AirportImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportAirportsCommandValidator : AbstractValidator<ImportAirportsCommand>
{
    public ImportAirportsCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Iata)
                .NotEmpty()
                .Length(3)
                .Matches("^[A-Z]{3}$").WithMessage("'{PropertyName}' must be a 3-letter uppercase IATA code.");
            item.RuleFor(d => d.Icao)
                .Length(4).When(d => d.Icao is not null)
                .Matches("^[A-Z]{4}$").When(d => d.Icao is not null)
                .WithMessage("'{PropertyName}' must be a 4-letter uppercase ICAO code.");
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.City).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.CountryCode)
                .NotEmpty()
                .Length(2)
                .Matches("^[A-Z]{2}$").WithMessage("'{PropertyName}' must be a 2-letter uppercase ISO country code.");
            item.RuleFor(d => d.Latitude)
                .InclusiveBetween(-90, 90).When(d => d.Latitude.HasValue);
            item.RuleFor(d => d.Longitude)
                .InclusiveBetween(-180, 180).When(d => d.Longitude.HasValue);
        });
    }
}

public sealed class ImportAirportsCommandHandler : IRequestHandler<ImportAirportsCommand, ImportResult>
{
    private readonly IAirportRepository _repo;
    public ImportAirportsCommandHandler(IAirportRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportAirportsCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var existing = await _repo.FindByIataAsync(dto.Iata, ct);
                if (existing is null)
                {
                    await _repo.AddAsync(Airport.Create(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode,
                        dto.Latitude, dto.Longitude, dto.ElevationFt, dto.Timezone), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode,
                        dto.Latitude, dto.Longitude, dto.ElevationFt, dto.Timezone);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Airport '{dto.Iata}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
