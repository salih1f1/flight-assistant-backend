using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AirportImportDto(
    string Iata, string? Icao, string Name, string City, string CountryCode,
    double? Latitude, double? Longitude, int? ElevationFt,
    DateTime? VerifiedAt = null, string? VerifiedBy = null, string? VerificationSource = null);

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
                .Matches("^[A-Z]{3}$", RegexOptions.IgnoreCase).WithMessage("'{PropertyName}' must be a 3-letter IATA code.");
            item.RuleFor(d => d.Icao)
                .Length(4).When(d => d.Icao is not null)
                .Matches("^[A-Z]{4}$", RegexOptions.IgnoreCase).When(d => d.Icao is not null)
                .WithMessage("'{PropertyName}' must be a 4-letter ICAO code.");
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.City).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.CountryCode)
                .NotEmpty()
                .Length(2)
                .Matches("^[A-Z]{2}$", RegexOptions.IgnoreCase).WithMessage("'{PropertyName}' must be a 2-letter ISO country code.");
            item.RuleFor(d => d.Latitude)
                .InclusiveBetween(-90, 90).When(d => d.Latitude.HasValue);
            item.RuleFor(d => d.Longitude)
                .InclusiveBetween(-180, 180).When(d => d.Longitude.HasValue);
            item.RuleFor(d => d.VerifiedBy).MaximumLength(255).When(d => d.VerifiedBy is not null);
            item.RuleFor(d => d.VerificationSource).MaximumLength(100).When(d => d.VerificationSource is not null);
        });
    }
}

public sealed class ImportAirportsCommandHandler : IRequestHandler<ImportAirportsCommand, ImportResult>
{
    private readonly IAirportRepository _repo;
    private readonly IAdminContext _adminContext;

    public ImportAirportsCommandHandler(IAirportRepository repo, IAdminContext adminContext)
    {
        _repo = repo;
        _adminContext = adminContext;
    }

    public async Task<ImportResult> Handle(ImportAirportsCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var verification = ImportVerification.Resolve(
                    dto.VerifiedAt, dto.VerifiedBy, dto.VerificationSource, _adminContext.ActorId);
                if (verification.IsRejected)
                {
                    errors.Add($"Airport '{dto.Iata}': {verification.RejectionReason}");
                    continue;
                }

                var existing = await _repo.FindByIataAsync(dto.Iata, ct);
                if (existing is null)
                {
                    var airport = Airport.Create(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode,
                        dto.Latitude, dto.Longitude, dto.ElevationFt);
                    verification.Apply(airport);
                    await _repo.AddAsync(airport, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode,
                        dto.Latitude, dto.Longitude, dto.ElevationFt);
                    verification.Apply(existing);
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
