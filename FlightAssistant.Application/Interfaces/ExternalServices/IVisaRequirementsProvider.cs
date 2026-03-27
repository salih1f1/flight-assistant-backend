namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    public record VisaRequirementResult(
        string PassportCountryCode,
        string DestinationCountryCode,
        string AdmissionType,         // e.g. "visa_free", "visa_on_arrival", "e_visa", "visa_required"
        int? StayDays,
        string? Notes,
        string? RawPayloadJson
    );

    public interface IVisaRequirementsProvider
    {
        Task<VisaRequirementResult?> GetRequirementsAsync(string passportCountryCode, string destinationCountryCode, CancellationToken ct = default);
    }
}
