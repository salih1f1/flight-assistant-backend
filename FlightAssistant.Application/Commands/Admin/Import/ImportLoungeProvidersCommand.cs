using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record LoungeProviderImportDto(string Code, string Name, string Kind, string? Description);

public record ImportLoungeProvidersCommand(IReadOnlyList<LoungeProviderImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportLoungeProvidersCommandValidator : AbstractValidator<ImportLoungeProvidersCommand>
{
    private static readonly string[] ValidKinds = Enum.GetNames<LoungeProviderKind>();

    public ImportLoungeProvidersCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code).NotEmpty().MaximumLength(32);
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.Kind)
                .Must(k => ValidKinds.Contains(k, StringComparer.OrdinalIgnoreCase))
                .WithMessage("'{PropertyName}' must be a valid LoungeProviderKind value.");
        });
    }
}

public sealed class ImportLoungeProvidersCommandHandler : IRequestHandler<ImportLoungeProvidersCommand, ImportResult>
{
    private readonly ILoungeRepository _repo;
    public ImportLoungeProvidersCommandHandler(ILoungeRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportLoungeProvidersCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                if (!Enum.TryParse<LoungeProviderKind>(dto.Kind, ignoreCase: true, out var kind))
                {
                    errors.Add($"LoungeProvider '{dto.Code}': unknown kind '{dto.Kind}' — skipped");
                    continue;
                }

                var existing = await _repo.FindProviderByCodeAsync(dto.Code, ct);
                if (existing is null)
                {
                    await _repo.AddProviderAsync(LoungeProvider.Create(dto.Code, dto.Name, kind, dto.Description), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name, kind, dto.Description);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"LoungeProvider '{dto.Code}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
