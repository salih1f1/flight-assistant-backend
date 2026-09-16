using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Application.FlightSync
{
    /// <summary>
    /// Records one provider status on a flight: the status row id (with the <c>UNK</c> fallback), the
    /// dedup hash, the event, and the flight's own push decision (#351, #352). Shared so that every way a
    /// status arrives stores the same thing by the same rules — the polling sync today, and Phase 2's
    /// alert webhook (#364) next — rather than each keeping a copy of these rules.
    /// </summary>
    public interface IFlightStatusApplier
    {
        /// <summary>
        /// Applies an already-fetched status. Makes no provider call and never saves: the caller owns the
        /// unit of work, so a poll and an alert can each choose their own transaction boundary.
        /// </summary>
        /// <param name="source">The provider name recorded on the event and hashed into its dedup hash —
        /// <see cref="FlightStatusSources.AeroApi"/> for AeroAPI. Every path recording a status from the same
        /// provider must pass the same constant, or stored hashes stop matching and each status is recorded twice.</param>
        /// <returns>Whether an event was recorded.</returns>
        Task<bool> ApplyAsync(
            Flight flight, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct = default);
    }
}
