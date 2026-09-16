using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Configuration for the reference-data verification job (#367, spec §4.6), from the
    /// <c>Verification</c> section. Bound and validated on start by
    /// <see cref="VerificationJobServiceCollectionExtensions.AddVerificationJob"/>, so an out-of-range
    /// value stops the host from starting instead of surfacing mid-cycle. Application reads it through
    /// <see cref="IVerificationJobOptions"/>.
    /// </summary>
    public sealed class VerificationOptions : IVerificationJobOptions, IValidatableObject
    {
        public const string SectionName = "Verification";

        /// <summary>Whether <see cref="ReferenceVerificationBackgroundService"/> runs at all. Defaults
        /// to <c>true</c>, like <c>FlightSync:Enabled</c>. A cycle with nothing queued claims nothing
        /// and downloads nothing.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The wait between the end of one cycle and the start of the next.</summary>
        [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How many requests one cycle claims at most.</summary>
        [Range(1, 100)]
        public int BatchSize { get; set; } = 10;

        /// <summary>How many source-error attempts a request gets before it completes as <c>Error</c>.</summary>
        [Range(1, 10)]
        public int MaxAttempts { get; set; } = 3;

        /// <summary>How long a source's downloaded dataset is kept after its last lookup once no open
        /// request uses the source, so a one-row check soon after another doesn't download it again.
        /// <c>00:00:00</c> releases it as soon as no open request does.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
        public TimeSpan IdleRelease { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>How old a verification may be before a tracked flight re-queues the entity
        /// (step 10). Not used by the job itself.</summary>
        [Range(1, 3650)]
        public int StaleAfterDays { get; set; } = 180;

        /// <summary>How long an entity whose most recent completed check was <c>Error</c> is left
        /// alone before the flight-tracking trigger re-queues it (#418). <see cref="TimeSpan.Zero"/>
        /// disables the cooldown. Not used by the job itself, which claims whatever is <c>Pending</c>
        /// regardless of any prior <c>Error</c> outcome.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
        public TimeSpan ErrorCooldown { get; set; } = TimeSpan.FromHours(6);

        /// <summary>The sources a check uses when its caller names none (steps 6 and 10). Not used by
        /// the job itself, which reads each request's own sources.</summary>
        public VerificationDefaultSources DefaultSources { get; set; } = new();

        /// <summary>The <see cref="IVerificationJobOptions"/> view of <see cref="DefaultSources"/>.</summary>
        public IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType) =>
            DefaultSources.For(entityType);

        // ValidateDataAnnotations does not descend into DefaultSources, so its values are checked here.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
            DefaultSources.Validate(nameof(DefaultSources));
    }

    /// <summary>
    /// The default sources per entity type (spec §4.6): one source name, or several joined with a
    /// comma in the order they are asked, e.g. <c>ourairports,aeroapi</c>. A name need not be
    /// registered yet (<c>aeroapi</c> arrives in step 9); the list must pass
    /// <see cref="VerificationRequest.TryJoinSources"/>, the same rule a request's sources do.
    /// </summary>
    public sealed class VerificationDefaultSources
    {
        public string Airport { get; set; } = "ourairports";
        public string Airline { get; set; } = "aeroapi";
        public string Country { get; set; } = "mledoze";

        /// <summary>The configured source names for <paramref name="entityType"/>, in order.</summary>
        public IReadOnlyList<string> For(VerificationEntityType entityType) =>
            Raw(entityType).Split(VerificationRequest.SourceSeparator);

        // The rule VerificationRequest.Create applies to a request's sources, so a default always queues.
        internal IEnumerable<ValidationResult> Validate(string prefix)
        {
            foreach (var entityType in Enum.GetValues<VerificationEntityType>())
            {
                var raw = Raw(entityType);
                if (!VerificationRequest.TryJoinSources(raw.Split(VerificationRequest.SourceSeparator), out _, out var error))
                    yield return new ValidationResult(
                        $"{prefix}:{entityType} is not a valid source list ('{raw}'): {error}",
                        new[] { $"{prefix}.{entityType}" });
            }
        }

        private string Raw(VerificationEntityType entityType) => entityType switch
        {
            VerificationEntityType.Airport => Airport ?? string.Empty,
            VerificationEntityType.Airline => Airline ?? string.Empty,
            VerificationEntityType.Country => Country ?? string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.")
        };
    }
}
