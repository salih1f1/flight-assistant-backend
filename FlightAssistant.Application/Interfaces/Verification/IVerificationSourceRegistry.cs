namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// Resolves an <see cref="IVerificationSource"/> by name, and releases the datasets sources are
    /// no longer needed to cache (#367 task 3, spec §4.3).
    ///
    /// <para>
    /// <b>Ownership (controller ruling).</b> This registry has no repository/EF dependency and never
    /// queries <c>verification_requests</c> itself. Task 4's job cycle is the one that asks
    /// <c>IVerificationRequestRepository</c> which sources still have an open request and passes
    /// that set to <see cref="ReleaseUnused"/> -- keeping this registry a pure name-to-source lookup
    /// that Application code can depend on without pulling in Infrastructure's persistence layer.
    /// </para>
    /// </summary>
    public interface IVerificationSourceRegistry
    {
        /// <summary>
        /// The source registered under <paramref name="name"/>, or null when no source is
        /// registered under that name. Null is deliberately distinguishable from every other
        /// outcome a lookup can have (found, not found, source error) -- a request naming an
        /// unregistered source (e.g. <c>aeroapi</c> before step 9) is Task 4's one immediate,
        /// non-retried <c>Error</c> (spec §4.2's Job outcomes table), which needs to be told apart
        /// from a source that answered but had no row, or a source that failed and should be retried.
        /// </summary>
        IVerificationSource? TryGet(string name);

        /// <summary>Every registered source. Lets a caller enumerate names (e.g. to ask
        /// <c>IVerificationRequestRepository.AnyOpenRequestUsesSourceAsync</c> about each one before
        /// calling <see cref="ReleaseUnused"/>) and costs without resolving each by name first.</summary>
        IReadOnlyCollection<IVerificationSource> Sources { get; }

        /// <summary>
        /// Calls <see cref="IVerificationSource.ReleaseCache"/> on every registered source whose
        /// <see cref="IVerificationSource.Name"/> is not in <paramref name="sourceNamesInUse"/>. A
        /// source already released (or never loaded) is left alone -- releasing is idempotent.
        /// Safe to call every job cycle regardless of whether anything actually needs releasing.
        /// </summary>
        /// <param name="sourceNamesInUse">Source names named by at least one open (Pending/Running)
        /// request, gathered by the caller from <c>IVerificationRequestRepository</c>.</param>
        void ReleaseUnused(IReadOnlyCollection<string> sourceNamesInUse);
    }
}
