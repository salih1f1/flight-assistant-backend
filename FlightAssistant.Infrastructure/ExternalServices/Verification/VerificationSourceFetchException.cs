namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// Internal control-flow exception for a verification source's download/parse pipeline
    /// (#367 task 3) -- never crosses <see cref="Application.Interfaces.Verification.IVerificationSource.LookupAsync"/>.
    /// A source's <c>LookupAsync</c> catches this (and any other non-cancellation exception the
    /// download or parse step raises) around its call into <see cref="VerificationDatasetCache{T}.GetAsync"/>
    /// and converts it to <see cref="Application.Interfaces.Verification.VerificationSourceFetchResult.SourceError"/>
    /// -- the public, typed shape Task 4's processor actually consumes. Using an exception here is
    /// purely an implementation convenience for unwinding a multi-step pipeline (send request, check
    /// status, read body, parse); it is deliberately not part of the public contract (see that
    /// interface's ownership remark on <c>IVerificationSource</c>).
    /// </summary>
    internal sealed class VerificationSourceFetchException : Exception
    {
        public VerificationSourceFetchException(string message) : base(message)
        {
        }

        public VerificationSourceFetchException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
