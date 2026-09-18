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

            Assert.Equal(1, reads.Count);
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

        // Diagnostics must not become an outage: a database blip must not stop a running job.
        [Fact]
        public async Task AFailedRead_LeavesTheJobEnabled()
        {
            var (sut, _, _) = Build(rows: [], throwOnRead: true);

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
