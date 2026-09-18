using FlightAssistant.Application.DataSources;
using Xunit;

namespace FlightAssistant.Application.Tests.DataSources
{
    public class DataSourceStatusEvaluatorTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void NeverObserved_IsUnknown_NotOk()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                lastOutcome: null, lastOkUtc: null, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Unknown, status);
        }

        [Fact]
        public void NotConfigured_StaysNotConfigured_NotUnhealthy()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.NotConfigured, lastOkUtc: null, DataSourceCriticality.Optional, Now);

            Assert.Equal(DataSourceStatusValue.NotConfigured, status);
        }

        [Fact]
        public void Ok_IsOk()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Ok, Now, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Ok, status);
        }

        // The case that motivated the whole issue: the old aeroapi check called a 401 "Healthy".
        [Fact]
        public void Unauthorized_OnRequiredSource_IsUnhealthy_NeverOk()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Unauthorized, lastOkUtc: null, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Unhealthy, status);
        }

        [Fact]
        public void Unauthorized_OnOptionalSource_IsDegraded()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Unauthorized, lastOkUtc: null, DataSourceCriticality.Optional, Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }

        [Fact]
        public void RateLimited_IsDegraded_NotUnhealthy()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.RateLimited, Now, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }

        [Fact]
        public void Failed_OnRequiredSource_WithRecentSuccess_IsDegraded()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Failed,
                lastOkUtc: Now.AddMinutes(-5),
                DataSourceCriticality.Required,
                Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }

        [Fact]
        public void Failed_OnRequiredSource_WithNoRecentSuccess_IsUnhealthy()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Failed,
                lastOkUtc: Now - DataSourceStatusEvaluator.RecentSuccessWindow.Add(TimeSpan.FromMinutes(1)),
                DataSourceCriticality.Required,
                Now);

            Assert.Equal(DataSourceStatusValue.Unhealthy, status);
        }

        [Fact]
        public void Failed_OnOptionalSource_IsDegraded_EvenWithNoRecentSuccess()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Failed, lastOkUtc: null, DataSourceCriticality.Optional, Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }
    }
}
