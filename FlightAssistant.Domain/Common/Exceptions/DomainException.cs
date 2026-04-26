namespace FlightAssistant.Domain.Common.Exceptions
{
    public class DomainException : Exception
    {
        public int StatusCode { get; }

        public DomainException(string message, int statusCode = 400) : base(message)
            => StatusCode = statusCode;

        public DomainException(string message, Exception inner, int statusCode = 400)
            : base(message, inner) => StatusCode = statusCode;
    }
}
