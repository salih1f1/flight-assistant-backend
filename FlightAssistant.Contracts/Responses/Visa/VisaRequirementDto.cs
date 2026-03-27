namespace FlightAssistant.Contracts.Responses.Visa
{
    public record VisaRequirementDto(
        string PassportCountryCode,
        string DestinationCountryCode,
        string AdmissionType,
        int? StayDays,
        string? Notes
    );
}
