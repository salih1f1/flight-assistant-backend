using FlightAssistant.Application.Interfaces.Verification;

namespace FlightAssistant.Infrastructure.ExternalServices.Verification
{
    /// <summary>
    /// Resolves an <see cref="IVerificationSource"/> by name and releases sources' cached datasets
    /// (#367 task 3, spec §4.3). Registered as a singleton over the same singleton source instances
    /// <c>DependencyInjection.AddInfrastructure</c> registers, so a source's cached dataset persists
    /// across job cycles (each of which runs in its own, short-lived DI scope) rather than being
    /// rebuilt every cycle.
    ///
    /// <para>
    /// Deliberately has no repository/EF dependency (controller ruling): it only reads the sources
    /// handed to it at construction. Task 4's job cycle is what asks
    /// <c>IVerificationRequestRepository</c> which sources still have an open request and passes
    /// that set to <see cref="ReleaseUnused"/>.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceRegistry : IVerificationSourceRegistry
    {
        private readonly IReadOnlyDictionary<string, IVerificationSource> _byName;

        public VerificationSourceRegistry(IEnumerable<IVerificationSource> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);
            _byName = sources.ToDictionary(s => s.Name, StringComparer.Ordinal);
            Sources = _byName.Values.ToList();
        }

        public IReadOnlyCollection<IVerificationSource> Sources { get; }

        public IVerificationSource? TryGet(string name) =>
            !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out var source) ? source : null;

        public void ReleaseUnused(IReadOnlyCollection<string> sourceNamesInUse)
        {
            ArgumentNullException.ThrowIfNull(sourceNamesInUse);
            var inUse = new HashSet<string>(sourceNamesInUse, StringComparer.Ordinal);

            foreach (var source in Sources)
            {
                if (!inUse.Contains(source.Name))
                    source.ReleaseCache();
            }
        }
    }
}
