using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Domain.Jobs.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>Upserts <c>job_switches</c>. One row per job — two today — so no paging or filtering
    /// is needed; see <see cref="IJobSwitchRepository"/> for why this exists apart from the cached
    /// read side.</summary>
    public sealed class JobSwitchRepository : IJobSwitchRepository
    {
        private readonly FlightAssistantDbContext _db;

        public JobSwitchRepository(FlightAssistantDbContext db) => _db = db;

        public async Task UpsertAsync(JobSwitch row, CancellationToken ct)
        {
            var existing = await _db.JobSwitches.FirstOrDefaultAsync(s => s.Job == row.Job, ct);

            if (existing is null)
            {
                _db.JobSwitches.Add(row);
            }
            else
            {
                existing.Enabled = row.Enabled;
                existing.ChangedUtc = row.ChangedUtc;
                existing.ChangedBy = row.ChangedBy;
                existing.Reason = row.Reason;
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
