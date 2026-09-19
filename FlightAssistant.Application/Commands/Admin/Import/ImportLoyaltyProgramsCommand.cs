using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record LoyaltyTierImportDto(string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);

public record LoyaltyProgramImportDto(
    string Code, string Name, string KindCode,
    string? AirlineIata,
    IReadOnlyList<LoyaltyTierImportDto> Tiers,
    DateTime? VerifiedAt = null, string? VerifiedBy = null, string? VerificationSource = null);

public record ImportLoyaltyProgramsCommand(IReadOnlyList<LoyaltyProgramImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportLoyaltyProgramsCommandValidator : AbstractValidator<ImportLoyaltyProgramsCommand>
{
    public ImportLoyaltyProgramsCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code).NotEmpty().MaximumLength(16);
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.KindCode).NotEmpty().MaximumLength(32);
            item.RuleFor(d => d.AirlineIata)
                .Matches("^[A-Z0-9]{2}$", RegexOptions.IgnoreCase).When(d => d.AirlineIata is not null)
                .WithMessage("'{PropertyName}' must be a 2-character IATA code.");
            item.RuleFor(d => d.Tiers).NotNull();
            item.RuleForEach(d => d.Tiers).ChildRules(t =>
            {
                t.RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
                t.RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
                t.RuleFor(x => x.Rank).GreaterThan(0);
            });
            item.RuleFor(d => d.VerifiedBy).MaximumLength(255).When(d => d.VerifiedBy is not null);
            item.RuleFor(d => d.VerificationSource).MaximumLength(100).When(d => d.VerificationSource is not null);
        });
    }
}

public sealed class ImportLoyaltyProgramsCommandHandler : IRequestHandler<ImportLoyaltyProgramsCommand, ImportResult>
{
    private readonly ILoyaltyRepository _loyaltyRepo;
    private readonly IRefDataRepository _refDataRepo;
    private readonly IAdminContext _adminContext;

    public ImportLoyaltyProgramsCommandHandler(ILoyaltyRepository loyaltyRepo, IRefDataRepository refDataRepo, IAdminContext adminContext)
    {
        _loyaltyRepo = loyaltyRepo;
        _refDataRepo = refDataRepo;
        _adminContext = adminContext;
    }

    public async Task<ImportResult> Handle(ImportLoyaltyProgramsCommand request, CancellationToken ct)
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
                    errors.Add($"LoyaltyProgram '{dto.Code}': {verification.RejectionReason}");
                    continue;
                }

                var kind = await _loyaltyRepo.FindKindByCodeAsync(dto.KindCode, ct);
                if (kind is null)
                {
                    errors.Add($"LoyaltyProgram '{dto.Code}': kind '{dto.KindCode}' not found — skipped");
                    continue;
                }

                int? airlineId = null;
                if (!string.IsNullOrWhiteSpace(dto.AirlineIata))
                {
                    airlineId = await _refDataRepo.GetAirlineIdByIataAsync(dto.AirlineIata, ct);
                    if (airlineId is null)
                    {
                        errors.Add($"LoyaltyProgram '{dto.Code}': airline '{dto.AirlineIata}' not found — skipped");
                        continue;
                    }
                }

                var existing = await _loyaltyRepo.FindProgramByCodeWithTiersAsync(dto.Code, ct);
                if (existing is null)
                {
                    var program = LoyaltyProgram.Create(dto.Code, dto.Name, kind.Id, airlineId);
                    foreach (var t in dto.Tiers)
                        program.AddTier(LoyaltyTier.Create(0, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode));
                    verification.Apply(program);
                    await _loyaltyRepo.AddProgramAsync(program, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Code, dto.Name, kind.Id, airlineId);
                    existing.ReplaceAllTiers(dto.Tiers.Select(t =>
                        LoyaltyTier.Create(existing.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)));
                    verification.Apply(existing);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"LoyaltyProgram '{dto.Code}': {ex.Message}");
            }
        }

        await _loyaltyRepo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
