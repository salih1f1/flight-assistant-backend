namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// A save carried an outcome for a verification request that changed in the database after it
    /// was read: the claim was lost, typically to the stuck-request reset and another claim (#367).
    /// Thrown by <c>IVerificationRequestRepository.SaveChangesAsync</c> in place of EF Core's
    /// <c>DbUpdateConcurrencyException</c>, which Application cannot reference.
    ///
    /// <para>
    /// Nothing from the failed save was written, including any entity <c>Verify</c> or
    /// <c>MarkVerificationFailed</c> and its audit row. The newer claim owns the request, so the
    /// caller discards its result and the scope's context, and does not record a source error.
    /// </para>
    /// </summary>
    public sealed class VerificationClaimLostException : Exception
    {
        public VerificationClaimLostException(IReadOnlyList<long> requestIds, Exception innerException)
            : base($"Verification request(s) {string.Join(", ", requestIds)} changed after they were read; the claim was lost.",
                innerException)
        {
            RequestIds = requestIds;
        }

        /// <summary>The requests whose stored row no longer matched what the save expected.</summary>
        public IReadOnlyList<long> RequestIds { get; }
    }
}
