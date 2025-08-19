namespace FlightAssistant.Domain.Common.Exceptions
{
    public sealed class InvalidEmailException : DomainException
    {
        public InvalidEmailException(string? email)
            : base($"Invalid email format: '{email}'.") { }
    }
}
