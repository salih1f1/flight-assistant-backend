using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AllianceImportDto(
    string Code, string Name,
    DateTime? VerifiedAt = null, string? VerifiedBy = null, string? VerificationSource = null);

public record ImportAlliancesCommand(IReadOnlyList<AllianceImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportAlliancesCommandValidator : AbstractValidator<ImportAlliancesCommand>
{
    public ImportAlliancesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code)
                .NotEmpty()
                .MaximumLength(8)
                .Matches("^[A-Z0-9]+$", RegexOptions.IgnoreCase).WithMessage("'{PropertyName}' must be alphanumeric.");
            item.RuleFor(d => d.Name)
                .NotEmpty()
                .MaximumLength(200);
            item.RuleFor(d => d.VerifiedBy).MaximumLength(255).When(d => d.VerifiedBy is not null);
            item.RuleFor(d => d.VerificationSource).MaximumLength(100).When(d => d.VerificationSource is not null);
        });
    }
}

public sealed class ImportAlliancesCommandHandler : IRequestHandler<ImportAlliancesCommand, ImportResult>
{
    private readonly IAllianceRepository _repo;
    private readonly IAdminContext _adminContext;

    public ImportAlliancesCommandHandler(IAllianceRepository repo, IAdminContext adminContext)
    {
        _repo = repo;
        _adminContext = adminContext;
    }

    public async Task<ImportResult> Handle(ImportAlliancesCommand request, CancellationToken ct)
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
                    errors.Add($"Alliance '{dto.Code}': {verification.RejectionReason}");
                    continue;
                }

                var existing = await _repo.FindByCodeAsync(dto.Code, ct);
                if (existing is null)
                {
                    var alliance = Alliance.Create(dto.Code, dto.Name);
                    verification.Apply(alliance);
                    await _repo.AddAsync(alliance, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name);
                    verification.Apply(existing);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Alliance '{dto.Code}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
