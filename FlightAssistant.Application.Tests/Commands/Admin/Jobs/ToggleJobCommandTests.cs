using FlightAssistant.Application.Commands.Admin.Jobs;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Jobs.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Commands.Admin.Jobs
{
    /// <summary>
    /// The handler's first Application-level tests (#540). Until this file it was covered only by
    /// <c>AdminJobEndpointTests</c>, which runs on the InMemory provider with the audit interceptor
    /// stripped (#319) — so nothing anywhere could observe what this handler writes to the audit
    /// log, or in what order.
    /// </summary>
    public class ToggleJobCommandTests
    {
        private readonly IJobSwitchRepository _repository = Substitute.For<IJobSwitchRepository>();
        private readonly IAdminAuditRepository _audit = Substitute.For<IAdminAuditRepository>();
        private readonly IAdminContext _adminContext = Substitute.For<IAdminContext>();
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

        /// <summary>Config absent means enabled, the same default the hosted services read.</summary>
        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        private ToggleJobCommandHandler Build(IConfiguration? configuration = null)
        {
            _adminContext.ActorId.Returns("oid-1");
            _adminContext.ActorName.Returns("Ada");
            _adminContext.CorrelationId.Returns("corr-1");
            return new ToggleJobCommandHandler(
                _repository, configuration ?? Config(), _adminContext, _audit, _time);
        }

        /// <summary>
        /// The point of #540. Both repositories resolve the same scoped DbContext, so staging the
        /// audit row before the switch write is what puts them in one transaction — auditing after
        /// the switch had already committed meant a failure between the two could leave a job
        /// switched with no record of who did it or why.
        ///
        /// <para>
        /// Pinned with <c>Received.InOrder</c> rather than left to the reading: moving the staging
        /// back below the upsert reopens the defect and every other assertion here still passes.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Handle_StagesTheAuditRowBeforeTheSwitchWrite()
        {
            await Build().Handle(new ToggleJobCommand(JobNames.FlightSync, false, "spending too much"), default);

            Received.InOrder(() =>
            {
                _audit.AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
                _repository.UpsertAsync(Arg.Any<JobSwitch>(), Arg.Any<CancellationToken>());
            });
        }

        [Fact]
        public async Task Handle_Valid_UpsertsTheSwitchAndWritesOneAuditRowCarryingTheReason()
        {
            var result = await Build().Handle(
                new ToggleJobCommand(JobNames.FlightSync, false, "spending too much"), default);

            await _repository.Received(1).UpsertAsync(
                Arg.Is<JobSwitch>(row =>
                    row.Job == JobNames.FlightSync &&
                    row.Enabled == false &&
                    row.ChangedBy == "Ada" &&
                    row.Reason == "spending too much"),
                Arg.Any<CancellationToken>());

            await _audit.Received(1).AddAsync(
                Arg.Is<AdminAuditLog>(log =>
                    log.EntityType == "JobSwitch" &&
                    log.EntityId == JobNames.FlightSync &&
                    log.Summary!.Contains("spending too much")),
                Arg.Any<CancellationToken>());

            Assert.False(result.SwitchEnabled);
            Assert.False(result.EffectiveEnabled);
        }

        /// <summary>
        /// The save stays, though the upsert's own <c>SaveChangesAsync</c> has already committed
        /// what was staged. It is a no-op today and insurance if <c>IJobSwitchRepository</c>'s
        /// implementation ever stops saving internally.
        /// </summary>
        [Fact]
        public async Task Handle_Valid_StillCallsSaveOnTheAuditRepository()
        {
            await Build().Handle(new ToggleJobCommand(JobNames.Verification, true, null), default);

            await _audit.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task Handle_UnknownJob_Throws404AndWritesNothing()
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                Build().Handle(new ToggleJobCommand("nope", false, null), default));

            Assert.Equal(404, ex.StatusCode);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<JobSwitch>(), Arg.Any<CancellationToken>());
            await _audit.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Config is a hard ceiling: 409, and nothing written at all — so a stored row always
        /// reflects a real operator decision rather than a rejected attempt. The audit half of that
        /// now needs its own assertion, because after #540 the staging happens earlier and is no
        /// longer guaranteed by statement order alone.
        /// </summary>
        [Fact]
        public async Task Handle_ConfigBarsTheJob_Throws409AndWritesNothing()
        {
            var handler = Build(Config(("FlightSync:Enabled", "false")));

            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                handler.Handle(new ToggleJobCommand(JobNames.FlightSync, true, null), default));

            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("FlightSync:Enabled", ex.Message);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<JobSwitch>(), Arg.Any<CancellationToken>());
            await _audit.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// No actor means no audit row, matching <c>AdminAuditInterceptor</c>'s own rule. The switch
        /// is still written — see #541 for why this branch is a gap worth closing rather than a
        /// behaviour to build on.
        /// </summary>
        [Fact]
        public async Task Handle_NoActor_WritesTheSwitchWithoutAnAuditRow()
        {
            _adminContext.ActorId.Returns((string?)null);
            _adminContext.ActorName.Returns((string?)null);

            await new ToggleJobCommandHandler(_repository, Config(), _adminContext, _audit, _time)
                .Handle(new ToggleJobCommand(JobNames.FlightSync, false, null), default);

            await _repository.Received(1).UpsertAsync(Arg.Any<JobSwitch>(), Arg.Any<CancellationToken>());
            await _audit.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        }
    }
}
