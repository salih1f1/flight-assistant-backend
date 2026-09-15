using System.Diagnostics.CodeAnalysis;
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
    /// retry. A claim that records nothing for over 10 minutes is returned to Pending by the job's
    /// stuck-request reset, also one SQL statement, which counts an attempt and completes the request
    /// as Error (<see cref="ClaimAbandonedError"/>) once attempts reach the job's maximum. A request is <see cref="IsOpen">open</see> while Pending or Running, and a partial
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

        /// <summary>The text before the attempt count in <see cref="ClaimAbandonedError"/>.</summary>
        public const string ClaimAbandonedErrorPrefix = "Claim abandoned: no result recorded after ";

        /// <summary>The text after the attempt count in <see cref="ClaimAbandonedError"/>.</summary>
        public const string ClaimAbandonedErrorSuffix = " attempts";

        /// <summary>
        /// The <see cref="Error"/> of a request the job's stuck-request reset completed as
        /// <see cref="VerificationRequestStatus.Error"/>: its last claim ended without a recorded
        /// result (a crash, or a failure that could not itself be recorded) and that brought
        /// <see cref="Attempts"/> to the job's maximum. The reset is one SQL statement, like the
        /// claim, so it builds this text from <see cref="ClaimAbandonedErrorPrefix"/> and
        /// <see cref="ClaimAbandonedErrorSuffix"/>; this method is the same text for C# callers.
        /// </summary>
        /// <param name="attempts">The request's <see cref="Attempts"/> after the reset counted the
        /// abandoned claim: every attempt so far, source errors included, not only abandoned claims.</param>
        public static string ClaimAbandonedError(int attempts) =>
            ClaimAbandonedErrorPrefix + attempts.ToString(CultureInfo.InvariantCulture) + ClaimAbandonedErrorSuffix;

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

        /// <summary>Attempts so far: one per source error, and one per claim the job's stuck-request
        /// reset found abandoned (so a request that never records a result still runs out of attempts).</summary>
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
        ///
        /// <para>
        /// <see cref="Create"/> throws on the same rule; callers that take keys from outside (the admin
        /// enqueue endpoint, #378) call this first, so a malformed key is a validation failure rather
        /// than an exception from deep inside the queue.
        /// </para>
        /// </summary>
        /// <param name="canonical">The stored form, when the key is valid.</param>
        /// <param name="error">Why the key is invalid, otherwise.</param>
        public static bool TryCanonicalEntityKey(
            VerificationEntityType entityType,
            string? entityKey,
            [NotNullWhen(true)] out string? canonical,
            [NotNullWhen(false)] out string? error)
        {
            canonical = null;

            // The reasons are read by admins (a 400's field errors), so they name the key, not the parameter.
            if (string.IsNullOrWhiteSpace(entityKey))
            {
                error = "Key cannot be blank.";
                return false;
            }
            if (char.IsWhiteSpace(entityKey[0]) || char.IsWhiteSpace(entityKey[^1]))
            {
                error = $"Key '{entityKey}' has surrounding whitespace.";
                return false;
            }

            switch (entityType)
            {
                case VerificationEntityType.Country:
                    var code = entityKey.ToUpperInvariant();
                    if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper))
                    {
                        error = $"A Country key must be a two-letter code; got '{entityKey}'.";
                        return false;
                    }
                    canonical = code;
                    error = null;
                    return true;

                case VerificationEntityType.Airport:
                case VerificationEntityType.Airline:
                    if (entityKey[0] == '0'
                        || !entityKey.All(char.IsAsciiDigit)
                        || !int.TryParse(entityKey, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        error = $"An {entityType} key must be a positive integer id with no sign or leading zeros; got '{entityKey}'.";
                        return false;
                    }
                    canonical = entityKey;
                    error = null;
                    return true;

                default:
                    error = $"Unknown entity type {entityType}.";
                    return false;
            }
        }

        private static string CanonicalEntityKey(VerificationEntityType entityType, string entityKey)
        {
            // Kept ahead of the rule so a null key still throws ArgumentNullException.
            ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

            return TryCanonicalEntityKey(entityType, entityKey, out var canonical, out var error)
                ? canonical
                : throw new ArgumentException(error, nameof(entityKey));
        }

        /// <summary>
        /// The one rule for a list of source names, used by <see cref="Create"/> and by the verification
        /// job's configured default sources, so a configured default can always be queued: at least one
        /// name; each not blank and free of whitespace, <see cref="SourceSeparator"/> and <c>+</c> (the
        /// separators of <see cref="Sources"/> and of <c>IVerifiable.VerificationSource</c>); duplicates
        /// dropped, first occurrence kept; at most <see cref="MaxSourcesLength"/> characters joined.
        /// </summary>
        /// <param name="joined">The <see cref="Sources"/> value, when the list is valid.</param>
        /// <param name="error">Why the list is invalid, otherwise.</param>
        public static bool TryJoinSources(
            IEnumerable<string?> sources,
            [NotNullWhen(true)] out string? joined,
            [NotNullWhen(false)] out string? error)
        {
            ArgumentNullException.ThrowIfNull(sources);
            joined = null;

            var names = new List<string>();
            foreach (var name in sources)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "A source name cannot be blank.";
                    return false;
                }
                if (name.Any(c => c == SourceSeparator || c == '+' || char.IsWhiteSpace(c)))
                {
                    error = $"Source name '{name}' cannot contain whitespace, '{SourceSeparator}' or '+'.";
                    return false;
                }
                if (!names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }

            if (names.Count == 0)
            {
                error = "At least one source is required.";
                return false;
            }

            var candidate = string.Join(SourceSeparator, names);
            if (candidate.Length > MaxSourcesLength)
            {
                error = $"The sources are {candidate.Length} characters joined; the limit is {MaxSourcesLength}.";
                return false;
            }

            joined = candidate;
            error = null;
            return true;
        }

        private static string JoinSources(IEnumerable<string> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);
            return TryJoinSources(sources, out var joined, out var error)
                ? joined
                : throw new ArgumentException(error, nameof(sources));
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
