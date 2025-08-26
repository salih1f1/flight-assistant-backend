namespace FlightAssistant.Domain.Common.Exceptions
{
    public sealed class CountryCodeInvalidException : DomainException
    {
        public CountryCodeInvalidException(string? code)
            : base($"Country code must be ISO-3166-1 alpha-2. Got '{code}'.") { }
    }
}
