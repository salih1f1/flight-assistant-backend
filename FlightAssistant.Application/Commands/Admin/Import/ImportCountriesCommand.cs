using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record CountryImportDto(
    string Code, string Name, string? Alpha3, int? NumericCode,
    string? Region, string? Subregion, bool? IsEU, bool? IsSchengen, bool? IsEEA,
    DateTime? VerifiedAt = null, string? VerifiedBy = null, string? VerificationSource = null);

public record ImportCountriesCommand(IReadOnlyList<CountryImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportCountriesCommandValidator : AbstractValidator<ImportCountriesCommand>
{
    public ImportCountriesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code)
                .NotEmpty()
                .Length(2)
                .Matches("^[A-Z]{2}$", RegexOptions.IgnoreCase).WithMessage("'{PropertyName}' must be a 2-letter ISO code.");
            item.RuleFor(d => d.Name)
                .NotEmpty()
                .MaximumLength(200);
            item.RuleFor(d => d.Alpha3)
                .Matches("^[A-Z]{3}$", RegexOptions.IgnoreCase).When(d => d.Alpha3 is not null)
                .WithMessage("'{PropertyName}' must be a 3-letter ISO code.");
            item.RuleFor(d => d.VerifiedBy).MaximumLength(255).When(d => d.VerifiedBy is not null);
            item.RuleFor(d => d.VerificationSource).MaximumLength(100).When(d => d.VerificationSource is not null);
        });
    }
}

public sealed class ImportCountriesCommandHandler : IRequestHandler<ImportCountriesCommand, ImportResult>
{
    private readonly ICountryRepository _repo;
    private readonly IAdminContext _adminContext;

    public ImportCountriesCommandHandler(ICountryRepository repo, IAdminContext adminContext)
    {
        _repo = repo;
        _adminContext = adminContext;
    }

    public async Task<ImportResult> Handle(ImportCountriesCommand request, CancellationToken ct)
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
                    errors.Add($"Country '{dto.Code}': {verification.RejectionReason}");
                    continue;
                }

                var existing = await _repo.FindByCodeAsync(dto.Code, ct);
                if (existing is null)
                {
                    var country = Country.Create(dto.Code, dto.Name, dto.Alpha3, dto.NumericCode,
                        dto.Region, dto.Subregion, dto.IsEU, dto.IsSchengen, dto.IsEEA);
                    verification.Apply(country);
                    await _repo.AddAsync(country, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name, dto.Alpha3, dto.NumericCode,
                        dto.Region, dto.Subregion, dto.IsEU, dto.IsSchengen, dto.IsEEA);
                    verification.Apply(existing);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Country '{dto.Code}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
