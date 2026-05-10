namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface IVerifiable
    {
        bool IsVerified { get; }
        DateTime? VerifiedAt { get; }
        string? VerifiedBy { get; }
        void Verify(string verifiedBy);
        void Unverify();
    }
}
