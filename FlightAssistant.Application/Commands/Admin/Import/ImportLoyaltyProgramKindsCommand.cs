using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record LoyaltyProgramKindImportDto(string Code, string Name, string? Description);

public record ImportLoyaltyProgramKindsCommand(IReadOnlyList<LoyaltyProgramKindImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportLoyaltyProgramKindsCommandValidator : AbstractValidator<ImportLoyaltyProgramKindsCommand>
{
    public ImportLoyaltyProgramKindsCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code).NotEmpty().MaximumLength(32);
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.Description).MaximumLength(500).When(d => d.Description is not null);
        });
    }
}

public sealed class ImportLoyaltyProgramKindsCommandHandler : IRequestHandler<ImportLoyaltyProgramKindsCommand, ImportResult>
{
    private readonly ILoyaltyRepository _repo;
    public ImportLoyaltyProgramKindsCommandHandler(ILoyaltyRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportLoyaltyProgramKindsCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var existing = await _repo.FindKindByCodeAsync(dto.Code, ct);
                if (existing is null)
                {
                    await _repo.AddKindAsync(LoyaltyProgramKind.Create(dto.Code, dto.Name, dto.Description), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name, dto.Description);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"LoyaltyProgramKind '{dto.Code}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
