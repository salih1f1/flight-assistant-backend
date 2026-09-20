using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// The sources to ask for one entity type, as an admin has chosen them or as configuration
    /// defaults them. Replaces direct <c>IVerificationJobOptions.DefaultSourcesFor</c> calls at the
    /// two sites that queue or provision; that method stays as the fallback.
    /// </summary>
    public interface IVerificationSourceDefaults
    {
        Task<IReadOnlyList<string>> ForAsync(VerificationEntityType entityType, CancellationToken ct);
    }
}
