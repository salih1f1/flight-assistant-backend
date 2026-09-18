using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Application.Queries.Admin.Jobs;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.Application.Tests.Queries.Admin.Jobs
{
    public class GetJobsQueryTests
    {
        /// <summary>Answers every job the same way — the theory below only ever inspects
        /// JobNames.FlightSync, so one fixed answer for whichever job name is asked about is
        /// enough.</summary>
        private sealed class FakeJobSwitch(bool enabled) : IJobSwitch
        {
            public Task<bool> IsEnabledAsync(string job, CancellationToken ct) => Task.FromResult(enabled);

            public Task<JobSwitchState> GetAsync(string job, CancellationToken ct) =>
                Task.FromResult(new JobSwitchState(job, enabled, null, null, null));
        }

        private static GetJobsQueryHandler BuildHandler(bool configEnabled, bool switchEnabled)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FlightSync:Enabled"] = configEnabled.ToString(),
                    ["Verification:Enabled"] = configEnabled.ToString(),
                })
                .Build();

            return new GetJobsQueryHandler(new FakeJobSwitch(switchEnabled), configuration);
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]   // config is a hard ceiling
        [InlineData(false, false, false)]
        public async Task EffectiveEnabled_IsConfigAndSwitch(bool config, bool @switch, bool expected)
        {
            var result = await BuildHandler(configEnabled: config, switchEnabled: @switch)
                .Handle(new GetJobsQuery(), CancellationToken.None);

            Assert.Equal(expected, result.Single(j => j.Job == JobNames.FlightSync).EffectiveEnabled);
        }

        [Fact]
        public async Task CanToggle_IsFalse_WhenConfigHasTheJobOff()
        {
            var result = await BuildHandler(configEnabled: false, switchEnabled: true)
                .Handle(new GetJobsQuery(), CancellationToken.None);

            Assert.False(result.Single(j => j.Job == JobNames.FlightSync).CanToggle);
        }

        [Fact]
        public async Task ReturnsBothJobs()
        {
            var result = await BuildHandler(configEnabled: true, switchEnabled: true)
                .Handle(new GetJobsQuery(), CancellationToken.None);

            Assert.Equal(JobNames.All.Count, result.Count);
        }
    }
}
