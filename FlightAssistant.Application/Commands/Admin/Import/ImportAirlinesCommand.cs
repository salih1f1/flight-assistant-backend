using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AirlineImportDto(
    string? Iata, string? Icao, string Name, string CountryCode, string? AllianceCode,
    DateTime? VerifiedAt = null, string? VerifiedBy = null, string? VerificationSource = null);

public record ImportAirlinesCommand(IReadOnlyList<AirlineImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportAirlinesCommandValidator : AbstractValidator<ImportAirlinesCommand>
{
    public ImportAirlinesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d)
                .Must(d => !string.IsNullOrWhiteSpace(d.Iata) || !string.IsNullOrWhiteSpace(d.Icao))
                .WithMessage("Each airline must have at least one of Iata or Icao.");
            item.RuleFor(d => d.Iata)
                .Matches("^[A-Z0-9]{2}$", RegexOptions.IgnoreCase).When(d => d.Iata is not null)
                .WithMessage("'{PropertyName}' must be a 2-character IATA code.");
            item.RuleFor(d => d.Icao)
                .Matches("^[A-Z]{3}$", RegexOptions.IgnoreCase).When(d => d.Icao is not null)
                .WithMessage("'{PropertyName}' must be a 3-letter ICAO code.");
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.CountryCode)
                .NotEmpty()
                .Matches("^[A-Z]{2}$", RegexOptions.IgnoreCase).WithMessage("'{PropertyName}' must be a 2-letter ISO country code.");
            item.RuleFor(d => d.AllianceCode)
                .MaximumLength(8).When(d => d.AllianceCode is not null);
            item.RuleFor(d => d.VerifiedBy).MaximumLength(255).When(d => d.VerifiedBy is not null);
            item.RuleFor(d => d.VerificationSource).MaximumLength(100).When(d => d.VerificationSource is not null);
        });
    }
}

public sealed class ImportAirlinesCommandHandler : IRequestHandler<ImportAirlinesCommand, ImportResult>
{
    private readonly IAirlineRepository _repo;
    private readonly IAdminContext _adminContext;

    public ImportAirlinesCommandHandler(IAirlineRepository repo, IAdminContext adminContext)
    {
        _repo = repo;
        _adminContext = adminContext;
    }

    public async Task<ImportResult> Handle(ImportAirlinesCommand request, CancellationToken ct)
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
                    errors.Add($"Airline '{dto.Iata ?? dto.Icao ?? dto.Name}': {verification.RejectionReason}");
                    continue;
                }

                Airline? existing = null;
                if (!string.IsNullOrWhiteSpace(dto.Iata))
                    existing = await _repo.FindByIataAsync(dto.Iata, ct);
                if (existing is null && !string.IsNullOrWhiteSpace(dto.Icao))
                    existing = await _repo.FindByIcaoAsync(dto.Icao, ct);

                if (existing is null)
                {
                    var airline = Airline.Create(dto.Iata, dto.Icao, dto.Name, dto.CountryCode, dto.AllianceCode);
                    verification.Apply(airline);
                    await _repo.AddAsync(airline, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Iata, dto.Icao, dto.Name, dto.CountryCode, dto.AllianceCode);
                    verification.Apply(existing);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Airline '{dto.Iata ?? dto.Icao ?? dto.Name}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
