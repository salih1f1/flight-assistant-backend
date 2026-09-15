using System.Globalization;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Domain.Verification.Entities
{
    /// <summary>
    /// One automated check of one reference-data row against one or more sources: a row of the
    /// <c>verification_requests</c> queue table (#367, spec §2.3). The table is also the check's
    /// history, which is why <c>AdminAuditInterceptor</c> does not audit it.
    ///
    /// <para>
    /// <b>Lifecycle.</b> <see cref="Create"/> makes it <see cref="VerificationRequestStatus.Pending"/>.
    /// The job claims it, setting <see cref="VerificationRequestStatus.Running"/> and
    /// <see cref="StartedUtc"/> in one SQL statement (<c>FOR UPDATE SKIP LOCKED</c>), which is why
    /// there is no domain method for that step. One outcome method then completes it
    /// (<see cref="MarkVerified"/>, <see cref="MarkMismatch"/>, <see cref="MarkNotFound"/>,
    /// <see cref="MarkError"/>), or <see cref="RecordSourceError"/> sends it back to Pending for a
    /// retry. A request is <see cref="IsOpen">open</see> while Pending or Running, and a partial
    /// unique index allows at most one open request per entity.
    /// </para>
    /// </summary>
    public sealed class VerificationRequest
    {
        /// <summary>Column size of <c>entity_key</c>.</summary>
        public const int MaxEntityKeyLength = 50;

        /// <summary>Column size of <c>sources</c> (the comma-joined names).</summary>
        public const int MaxSourcesLength = 100;

        /// <summary>Column size of <c>requested_by</c>.</summary>
        public const int MaxRequestedByLength = 255;

        /// <summary>Column size of <c>error</c>; longer errors are truncated to it.</summary>
        public const int MaxErrorLength = 1000;

        /// <summary>Joins source names in <see cref="Sources"/>.</summary>
        public const char SourceSeparator = ',';

        public long Id { get; private set; }

        public VerificationEntityType EntityType { get; private set; }

        /// <summary>The entity's key in canonical form: the positive integer id (no sign, no leading
        /// zeros) for airports and airlines, the upper-case two-letter code for countries.</summary>
        public string EntityKey { get; private set; } = default!;

        /// <summary>The requested source names, comma-joined in request order, e.g. <c>ourairports,aeroapi</c>.
        /// <see cref="SourceNames"/> is the parsed form.</summary>
        public string Sources { get; private set; } = default!;

        public VerificationTrigger Trigger { get; private set; }

        public VerificationRequestStatus Status { get; private set; }

        /// <summary>Source-error attempts so far. The job's stuck-request reset does not change it.</summary>
        public int Attempts { get; private set; }

        /// <summary>When a retried request may be claimed again. Null when it can be claimed now, and on every completed request.</summary>
        public DateTime? NextAttemptUtc { get; private set; }

        /// <summary><c>jsonb</c>: <c>[{ "column", "ours", "source", "sourceName" }]</c>. Set only by <see cref="MarkMismatch"/>.</summary>
        public string? DifferencesJson { get; private set; }

        /// <summary><c>jsonb</c>: what each source returned, keyed by source name. Set by <see cref="MarkMismatch"/> and <see cref="MarkNotFound"/>.</summary>
        public string? SourceValuesJson { get; private set; }

        /// <summary>The last source error, at most <see cref="MaxErrorLength"/> characters. Kept while a
        /// retry is pending and on <see cref="VerificationRequestStatus.Error"/>; cleared by the other outcomes.</summary>
        public string? Error { get; private set; }

        /// <summary>Sum of each source's per-row cost; 0 for the free sources.</summary>
        public decimal EstimatedCostUsd { get; private set; }

        /// <summary>The admin's id, or <c>system</c>.</summary>
        public string RequestedBy { get; private set; } = default!;

        public DateTime RequestedUtc { get; private set; }

        /// <summary>When the current claim started. Null while Pending.</summary>
        public DateTime? StartedUtc { get; private set; }

        /// <summary>Set by every outcome that completes the request.</summary>
        public DateTime? CompletedUtc { get; private set; }

        /// <summary><see cref="Sources"/> split into names, in request order. Not mapped.</summary>
        public IReadOnlyList<string> SourceNames => Sources.Split(SourceSeparator);

        /// <summary>Pending or Running. Not mapped.</summary>
        public bool IsOpen => Status is VerificationRequestStatus.Pending or VerificationRequestStatus.Running;

        private VerificationRequest() { } // EF

        /// <summary>
        /// A new <see cref="VerificationRequestStatus.Pending"/> request with no attempts.
        /// </summary>
        /// <param name="entityKey">Canonicalised: a Country code is upper-cased; an Airport or Airline id must
        /// already be canonical decimal. Empty, surrounding-whitespace or malformed keys throw.</param>
        /// <param name="sources">One or more source names. Duplicates are dropped (first occurrence
        /// kept); a name may not be blank or contain whitespace, <c>,</c> or <c>+</c> (the separators
        /// of <see cref="Sources"/> and of <c>IVerifiable.VerificationSource</c>).</param>
        /// <exception cref="ArgumentException">An argument is blank, too long for its column, not UTC, or out of range.</exception>
        public static VerificationRequest Create(
            VerificationEntityType entityType,
            string entityKey,
            IEnumerable<string> sources,
            VerificationTrigger trigger,
            string requestedBy,
            decimal estimatedCostUsd,
            DateTime requestedUtc)
        {
            if (!Enum.IsDefined(entityType))
                throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.");
            if (!Enum.IsDefined(trigger))
                throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger.");

            var canonicalKey = CanonicalEntityKey(entityType, entityKey);
            var joinedSources = JoinSources(sources);

            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            EnsureMaxLength(requestedBy, MaxRequestedByLength, nameof(requestedBy));

            ArgumentOutOfRangeException.ThrowIfNegative(estimatedCostUsd);
            EnsureUtc(requestedUtc, nameof(requestedUtc));

            return new VerificationRequest
            {
                EntityType = entityType,
                EntityKey = canonicalKey,
                Sources = joinedSources,
                Trigger = trigger,
                Status = VerificationRequestStatus.Pending,
                Attempts = 0,
                EstimatedCostUsd = estimatedCostUsd,
                RequestedBy = requestedBy,
                RequestedUtc = requestedUtc
            };
        }

        /// <summary>Every selected source matched: <see cref="VerificationRequestStatus.Verified"/>.</summary>
        /// <exception cref="InvalidOperationException">The request is already completed.</exception>
        public void MarkVerified(DateTime nowUtc)
        {
            EnsureOpen();
            EnsureUtc(nowUtc, nameof(nowUtc));

            Error = null;
            Complete(VerificationRequestStatus.Verified, nowUtc);
        }

        /// <summary>At least one source disagreed: <see cref="VerificationRequestStatus.Mismatch"/>,
        /// storing the differences and what each source returned.</summary>
        /// <exception cref="InvalidOperationException">The request is already completed.</exception>
        public void MarkMismatch(string differencesJson, string sourceValuesJson, DateTime nowUtc)
        {
            EnsureOpen();
            ArgumentException.ThrowIfNullOrWhiteSpace(differencesJson);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceValuesJson);
            EnsureUtc(nowUtc, nameof(nowUtc));

            DifferencesJson = differencesJson;
            SourceValuesJson = sourceValuesJson;
            Error = null;
            Complete(VerificationRequestStatus.Mismatch, nowUtc);
        }

        /// <summary>At least one source had no row: <see cref="VerificationRequestStatus.NotFound"/>,
        /// storing what the sources that did answer returned (<c>{}</c> when none did).</summary>
        /// <exception cref="InvalidOperationException">The request is already completed.</exception>
        public void MarkNotFound(string sourceValuesJson, DateTime nowUtc)
        {
            EnsureOpen();
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceValuesJson);
            EnsureUtc(nowUtc, nameof(nowUtc));

            DifferencesJson = null;
            SourceValuesJson = sourceValuesJson;
            Error = null;
            Complete(VerificationRequestStatus.NotFound, nowUtc);
        }

        /// <summary>
        /// A retryable source failure. Increments <see cref="Attempts"/> and stores
        /// <paramref name="error"/> (truncated). If <see cref="Attempts"/> has reached
        /// <paramref name="maxAttempts"/>, the request completes as
        /// <see cref="VerificationRequestStatus.Error"/>; otherwise it goes back to
        /// <see cref="VerificationRequestStatus.Pending"/> with <see cref="NextAttemptUtc"/> =
        /// <paramref name="nowUtc"/> + <paramref name="backoff"/> and <see cref="StartedUtc"/> cleared.
        /// </summary>
        /// <exception cref="InvalidOperationException">The request is already completed.</exception>
        public void RecordSourceError(string error, DateTime nowUtc, int maxAttempts, TimeSpan backoff)
        {
            EnsureOpen();
            ArgumentException.ThrowIfNullOrWhiteSpace(error);
            EnsureUtc(nowUtc, nameof(nowUtc));
            ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(backoff, TimeSpan.Zero);

            Attempts++;
            Error = Truncate(error);

            if (Attempts >= maxAttempts)
            {
                Complete(VerificationRequestStatus.Error, nowUtc);
                return;
            }

            Status = VerificationRequestStatus.Pending;
            NextAttemptUtc = nowUtc + backoff;
            StartedUtc = null;
        }

        /// <summary>A failure that is not retried (entity deleted, unknown source):
        /// <see cref="VerificationRequestStatus.Error"/> straight away, with <paramref name="error"/> truncated.</summary>
        /// <exception cref="InvalidOperationException">The request is already completed.</exception>
        public void MarkError(string error, DateTime nowUtc)
        {
            EnsureOpen();
            ArgumentException.ThrowIfNullOrWhiteSpace(error);
            EnsureUtc(nowUtc, nameof(nowUtc));

            Error = Truncate(error);
            Complete(VerificationRequestStatus.Error, nowUtc);
        }

        private void Complete(VerificationRequestStatus status, DateTime nowUtc)
        {
            Status = status;
            NextAttemptUtc = null;
            CompletedUtc = nowUtc;
        }

        // A completed request is history; changing its outcome would rewrite it.
        private void EnsureOpen()
        {
            if (!IsOpen)
                throw new InvalidOperationException(
                    $"Verification request {Id} is already {Status}; a completed request cannot change outcome.");
        }

        /// <summary>
        /// The one stored form of an entity key, so the open-request unique index sees one entity as
        /// one key: <c>Country</c> is a two-letter code, upper-cased; <c>Airport</c> and <c>Airline</c>
        /// are a positive <see cref="int"/> id in canonical decimal form (ASCII digits, no sign, no
        /// leading zeros). Empty keys and keys with surrounding whitespace are rejected, not trimmed.
        /// </summary>
        private static string CanonicalEntityKey(VerificationEntityType entityType, string entityKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
            if (char.IsWhiteSpace(entityKey[0]) || char.IsWhiteSpace(entityKey[^1]))
                throw new ArgumentException($"entityKey '{entityKey}' has surrounding whitespace.", nameof(entityKey));

            switch (entityType)
            {
                case VerificationEntityType.Country:
                    var code = entityKey.ToUpperInvariant();
                    if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper))
                        throw new ArgumentException($"A Country key must be a two-letter code; got '{entityKey}'.", nameof(entityKey));
                    return code;

                case VerificationEntityType.Airport:
                case VerificationEntityType.Airline:
                    if (entityKey[0] == '0'
                        || !entityKey.All(char.IsAsciiDigit)
                        || !int.TryParse(entityKey, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        throw new ArgumentException(
                            $"An {entityType} key must be a positive integer id with no sign or leading zeros; got '{entityKey}'.",
                            nameof(entityKey));
                    return entityKey;

                default:
                    throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.");
            }
        }

        private static string JoinSources(IEnumerable<string> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);

            var names = new List<string>();
            foreach (var name in sources)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A source name cannot be blank.", nameof(sources));
                if (name.Any(c => c == SourceSeparator || c == '+' || char.IsWhiteSpace(c)))
                    throw new ArgumentException(
                        $"Source name '{name}' cannot contain whitespace, '{SourceSeparator}' or '+'.", nameof(sources));
                if (!names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }

            if (names.Count == 0)
                throw new ArgumentException("At least one source is required.", nameof(sources));

            var joined = string.Join(SourceSeparator, names);
            EnsureMaxLength(joined, MaxSourcesLength, nameof(sources));
            return joined;
        }

        private static string Truncate(string error)
        {
            if (error.Length <= MaxErrorLength) return error;

            // Never cut between the two halves of a surrogate pair: a lone high surrogate is not
            // valid UTF-16, and Npgsql would fail to encode the whole save.
            var length = char.IsHighSurrogate(error[MaxErrorLength - 1]) ? MaxErrorLength - 1 : MaxErrorLength;
            return error[..length];
        }

        private static void EnsureMaxLength(string value, int maxLength, string paramName)
        {
            if (value.Length > maxLength)
                throw new ArgumentException($"{paramName} is {value.Length} characters; the limit is {maxLength}.", paramName);
        }

        private static void EnsureUtc(DateTime value, string paramName)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException($"{paramName} must have DateTimeKind.Utc, not {value.Kind}.", paramName);
        }
    }
}
