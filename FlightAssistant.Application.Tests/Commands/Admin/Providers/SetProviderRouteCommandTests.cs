using FlightAssistant.Application.Commands.Admin.Providers;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Providers.Entities;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Commands.Admin.Providers
{
    public class SetProviderRouteCommandTests
    {
        private readonly IProviderRouteRepository _repository = Substitute.For<IProviderRouteRepository>();
        private readonly IAdminAuditRepository _audit = Substitute.For<IAdminAuditRepository>();
        private readonly IProviderRouteDefaults _defaults = Substitute.For<IProviderRouteDefaults>();
        private readonly IAdminContext _adminContext = Substitute.For<IAdminContext>();
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        private SetProviderRouteCommandHandler Build()
        {
            _adminContext.ActorId.Returns("oid-1");
            _adminContext.ActorName.Returns("Ada");
            _adminContext.CorrelationId.Returns("corr-1");
            _defaults.For(ProviderJobs.FlightSearchRoute)
                     .Returns(new[] { ProviderJobs.AeroDataBox });
            return new SetProviderRouteCommandHandler(
                _repository, _defaults, _adminContext, _audit, _time);
        }

        [Fact]
        public async Task Handle_UnknownJob_Throws404()
        {
            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                handler.Handle(new SetProviderRouteCommand("nope", new[] { "aeroapi" }, null), default));
            Assert.Equal(404, ex.StatusCode);
        }

        [Fact]
        public async Task Handle_ProviderNotAnOptionForTheJob_Throws400()
        {
            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, new[] { "opensky" }, null), default));
            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("opensky", ex.Message);
        }

        [Fact]
        public async Task Handle_ConfigBarsEveryAlternative_Throws409AndWritesNothing()
        {
            // AeroDataBox:ApiKey unset leaves AeroApi as the only reachable provider, so the row
            // would be a decision the deployment cannot honour. 409, not 403: the caller is
            // permitted, the environment is what refuses. Nothing is written, so a stored row always
            // reflects a real operator decision rather than a rejected attempt.
            _defaults.For(ProviderJobs.FlightSearchRoute).Returns(new[] { ProviderJobs.AeroApi });
            _defaults.IsCeilinged(ProviderJobs.FlightSearchRoute).Returns(true);

            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroDataBox }, null),
                default));

            Assert.Equal(409, ex.StatusCode);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<ProviderRoute>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task Handle_Valid_UpsertsTheRowAndWritesOneAuditRowCarryingTheReason()
        {
            var handler = Build();

            var result = await handler.Handle(new SetProviderRouteCommand(
                ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroApi }, "quota gone"), default);

            await _repository.Received(1).UpsertAsync(
                Arg.Is<ProviderRoute>(row =>
                    row.Job == ProviderJobs.FlightSearchRoute &&
                    row.Providers == ProviderJobs.AeroApi &&
                    row.ChangedBy == "Ada" &&
                    row.Reason == "quota gone"),
                Arg.Any<CancellationToken>());

            await _audit.Received(1).AddAsync(
                Arg.Is<AdminAuditLog>(log =>
                    log.EntityType == "ProviderRoute" &&
                    log.EntityId == ProviderJobs.FlightSearchRoute &&
                    log.Summary!.Contains("quota gone")),
                Arg.Any<CancellationToken>());
            await _audit.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

            Assert.Equal(new[] { ProviderJobs.AeroApi }, result.Providers);
            Assert.False(result.IsDefault);
        }
    }
}
