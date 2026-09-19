using FlightAssistant.Domain.Jobs.Entities;

namespace FlightAssistant.Application.Interfaces.Jobs
{
    /// <summary>
    /// Write side for <c>job_switches</c>. Kept apart from <see cref="IJobSwitch"/> (Task 3's read
    /// side, cached and deliberately fail-open — see its own remarks) so a toggle write can never
    /// inherit that policy: a write must always reach the database, never be served from a stale
    /// cache entry, and never silently "succeed" against a database it never actually reached.
    /// </summary>
    public interface IJobSwitchRepository
    {
        /// <summary>Inserts or overwrites the row for <c>row.Job</c>.</summary>
        Task UpsertAsync(JobSwitch row, CancellationToken ct);
    }
}
