namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// The verification job's settings that Application code reads (#367, spec §4.6). The API-layer
    /// <c>VerificationOptions</c> implements this and is bound and validated on start there;
    /// Application can't reference that class, so it reads it through this interface (the
    /// <c>IFlightSearchHorizonOptions</c> translation pattern).
    /// </summary>
    public interface IVerificationJobOptions
    {
        /// <summary>How many requests one cycle claims at most.</summary>
        int BatchSize { get; }

        /// <summary>How many source-error attempts a request gets before it completes as
        /// <c>Error</c>.</summary>
        int MaxAttempts { get; }

        /// <summary>How long a source's downloaded dataset is kept after its last lookup once no open
        /// request uses the source (#378). <see cref="TimeSpan.Zero"/> releases it as soon as none
        /// does. Never extends the 24-hour maximum age, and a cached failure keeps only its cooldown.</summary>
        TimeSpan IdleRelease { get; }
    }
}
