using FlightAssistant.Application.Jobs;
using FlightAssistant.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.Jobs
{
    public class CachedJobSwitchTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task AJobWithNoRow_IsEnabled()
        {
            // An upgrade must not stop a running job. A missing row means "nobody has ever
            // touched this switch", which is enabled.
            var (sut, _, _) = Build(rows: []);

            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        [Fact]
        public async Task AJobSwitchedOff_IsNotEnabled()
        {
            var (sut, _, _) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, false)]);

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        [Fact]
        public async Task RepeatedReadsInsideTheWindow_HitTheDatabaseOnce()
        {
            var (sut, _, reads) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, true)]);

            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);

            Assert.Single(reads);
        }

        // The window is what bounds how long a toggle takes to take effect, so it is asserted
        // rather than left to chance.
        [Fact]
        public async Task AfterTheWindow_TheDatabaseIsReadAgain()
        {
            var (sut, time, reads) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, true)]);
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);

            time.Advance(CachedJobSwitch.CacheWindow + TimeSpan.FromSeconds(1));
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);

            Assert.Equal(2, reads.Count);
        }

        [Fact]
        public async Task ATogglePastTheWindow_IsObserved()
        {
            var rows = new List<JobSwitchRow> { new(JobNames.FlightSync, true) };
            var (sut, time, _) = Build(rows);
            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));

            rows[0] = new JobSwitchRow(JobNames.FlightSync, false);
            time.Advance(CachedJobSwitch.CacheWindow + TimeSpan.FromSeconds(1));

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        // Each job caches separately, or switching one off would silently stop the other for a
        // window.
        [Fact]
        public async Task EachJobIsCachedSeparately()
        {
            var (sut, _, _) = Build(rows:
            [
                new JobSwitchRow(JobNames.FlightSync, false),
                new JobSwitchRow(JobNames.Verification, true),
            ]);

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
            Assert.True(await sut.IsEnabledAsync(JobNames.Verification, CancellationToken.None));
        }

        // Cold start: an operator switched this job off, the API restarts, and the very first read of
        // this job's row throws -- e.g. the Npgsql pool is still warming and DataSourceSeeder is
        // contending for it. Failing open here would run a full cycle against AeroAPI for a job
        // someone deliberately stopped. Nothing has yet confirmed the job should run, so this fails
        // closed and skips the tick instead.
        [Fact]
        public async Task AFirstEverFailedRead_DoesNotEnableTheJob()
        {
            var (sut, _, _) = Build(rows: [], throwOnRead: true);

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        // Steady state: diagnostics must not become an outage, so once this job's read has worked at
        // least once, a later blip must not be able to stop it. Unlike Build's shared throwOnRead
        // (fixed for the sut's whole lifetime), this test needs a read that succeeds and THEN starts
        // failing, so it wires its own reader rather than reusing Build -- seeding a real success is
        // what tells this apart from AFirstEverFailedRead_DoesNotEnableTheJob just above, which is
        // the cold-start case this same fix now handles differently.
        [Fact]
        public async Task AFailedRead_LeavesTheJobEnabled()
        {
            var time = new FakeTimeProvider(Start);
            var throwOnRead = false;

            JobSwitchReader read = (job, _) =>
            {
                if (throwOnRead) throw new InvalidOperationException("Simulated database failure.");
                return Task.FromResult<JobSwitchState?>(new JobSwitchState(job, true, null, null, null));
            };

            var sut = new CachedJobSwitch(read, time, NullLogger<CachedJobSwitch>.Instance);
            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));

            // Past the cache window, so the next call is a real read rather than a cache hit.
            time.Advance(CachedJobSwitch.CacheWindow + TimeSpan.FromSeconds(1));
            throwOnRead = true;

            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        [Fact]
        public async Task GetAsync_IsNotCached()
        {
            var (sut, _, reads) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, true)]);

            await sut.GetAsync(JobNames.FlightSync, CancellationToken.None);
            await sut.GetAsync(JobNames.FlightSync, CancellationToken.None);

            Assert.Equal(2, reads.Count);
        }

        /// <summary>One in-memory row, as the fake reader below serves it to <see cref="CachedJobSwitch"/>.
        /// Deliberately just Job/Enabled -- no test here needs ChangedUtc/ChangedBy/Reason to pin the
        /// caching policy, so <see cref="Build"/> fills those with null.</summary>
        private sealed record JobSwitchRow(string Job, bool Enabled);

        /// <summary>
        /// Builds a <see cref="CachedJobSwitch"/> wired to an in-memory fake reader instead of a real
        /// database -- these tests are about the caching policy around the read, not the read itself.
        /// The fake reads <paramref name="rows"/> live on every call rather than snapshotting it at
        /// build time, so a test can mutate the list after building <c>sut</c> to simulate an admin
        /// toggling the switch between reads (<see cref="ATogglePastTheWindow_IsObserved"/>); it also
        /// appends the looked-up job name to the returned <c>Reads</c> list every time it is actually
        /// invoked, so a test can assert how many times the "database" was hit.
        /// </summary>
        private static (CachedJobSwitch Sut, FakeTimeProvider Time, List<string> Reads) Build(
            List<JobSwitchRow> rows, bool throwOnRead = false)
        {
            var time = new FakeTimeProvider(Start);
            var reads = new List<string>();

            JobSwitchReader read = (job, _) =>
            {
                reads.Add(job);
                if (throwOnRead) throw new InvalidOperationException("Simulated database failure.");

                var row = rows.FirstOrDefault(r => r.Job == job);
                JobSwitchState? state = row is null ? null : new JobSwitchState(row.Job, row.Enabled, null, null, null);
                return Task.FromResult(state);
            };

            var sut = new CachedJobSwitch(read, time, NullLogger<CachedJobSwitch>.Instance);
            return (sut, time, reads);
        }
    }
}
