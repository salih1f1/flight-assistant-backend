namespace FlightAssistant.Domain.Common.Exceptions
{
    public class DomainException : Exception
    {
        public int StatusCode { get; }

        /// <summary>
        /// Operator-facing explanation of the underlying cause — for example the error code and
        /// message an upstream provider returned. Deliberately kept out of <see cref="Exception.Message"/>
        /// so it is never surfaced to end users by default: the API only renders it outside of
        /// Production (see <c>GlobalExceptionHandler</c>). Never put credentials, request URLs, or
        /// anything else secret in here.
        /// </summary>
        public string? DiagnosticDetail { get; }

        public DomainException(string message, int statusCode = 400, string? diagnosticDetail = null)
            : base(message)
        {
            StatusCode = statusCode;
            DiagnosticDetail = diagnosticDetail;
        }

        public DomainException(string message, Exception inner, int statusCode = 400, string? diagnosticDetail = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            DiagnosticDetail = diagnosticDetail;
        }
    }
}
