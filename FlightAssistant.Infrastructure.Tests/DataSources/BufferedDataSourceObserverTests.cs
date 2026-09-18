using FlightAssistant.Application.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    public class BufferedDataSourceObserverTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        private static (BufferedDataSourceObserver Observer, FakeTimeProvider Time) Build()
        {
            var time = new FakeTimeProvider(Start);
            return (new BufferedDataSourceObserver(time), time);
        }

        [Fact]
        public void AFirstObservation_IsPendingImmediately()
        {
            var (observer, _) = Build();

            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);

            Assert.Single(observer.TakePending());
        }

        [Fact]
        public void TakePending_DrainsTheBuffer()
        {
            var (observer, _) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);

            observer.TakePending();

            Assert.Empty(observer.TakePending());
        }

        [Fact]
        public void RepeatedOkInsideTheWindow_Coalesces()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromMinutes(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Empty(observer.TakePending());
        }

        [Fact]
        public void RepeatedOkPastTheWindow_WritesAgain()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(BufferedDataSourceObserver.CoalesceWindow + TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Single(observer.TakePending());
        }

        [Fact]
        public void AFailureInsideTheWindow_IsNeverCoalesced()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow(), "aeroapi returned HTTP 503");

            var pending = observer.TakePending();
            Assert.Single(pending);
            Assert.Equal("Failed", pending.Single().LastOutcome);
        }

        /// <summary>
        /// The clause the plan's own break test could not reach: a second failure inside the window
        /// does not change the outcome, so only the "anything that is not Ok writes at once" rule
        /// keeps it visible. Without it a source that keeps failing would go quiet after the first
        /// write, and the climbing failure count would not reach the page for five minutes.
        /// </summary>
        [Fact]
        public void ARepeatedFailureInsideTheWindow_IsStillWritten()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Failed, Start, "aeroapi returned HTTP 503");
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow(), "aeroapi returned HTTP 503");

            var pending = observer.TakePending();
            Assert.Single(pending);
            Assert.Equal(2, pending.Single().ConsecutiveFailures);
        }

        [Fact]
        public void AnOutcomeChangeInsideTheWindow_IsNeverCoalesced()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Failed, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Single(observer.TakePending());
        }

        [Fact]
        public void ConsecutiveFailures_CountUp_AndResetOnOk()
        {
            var (observer, time) = Build();

            observer.Record("aeroapi", DataSourceOutcome.Failed, Start);
            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow());

            Assert.Equal(2, observer.TakePending().Single().ConsecutiveFailures);

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Equal(0, observer.TakePending().Single().ConsecutiveFailures);
        }

        [Fact]
        public void LastOkUtc_SurvivesALaterFailure()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow());

            Assert.Equal(Start.UtcDateTime, observer.TakePending().Single().LastOkUtc);
        }

        [Fact]
        public void Record_NeverThrows_OnAnUnknownKey()
        {
            var (observer, _) = Build();

            // Diagnostics must never fail the call they describe.
            observer.Record("", DataSourceOutcome.Ok, Start);
            observer.Record(null!, DataSourceOutcome.Ok, Start);
        }
    }
}
