using FlightAssistant.Application.Commands.Admin.Providers;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Providers.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Commands.Admin.Providers
{
    public class SetProviderRouteCommandTests
    {
        private readonly IProviderRouteRepository _repository = Substitute.For<IProviderRouteRepository>();
        private readonly IAdminAuditRepository _audit = Substitute.For<IAdminAuditRepository>();
        private readonly IProviderRouteDefaults _defaults = Substitute.For<IProviderRouteDefaults>();
        private readonly IVerificationSourceRegistry _registry = Substitute.For<IVerificationSourceRegistry>();
        private readonly IAdminContext _adminContext = Substitute.For<IAdminContext>();
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        private SetProviderRouteCommandHandler Build()
        {
            _adminContext.ActorId.Returns("oid-1");
            _adminContext.ActorName.Returns("Ada");
            _adminContext.CorrelationId.Returns("corr-1");
            _defaults.For(ProviderJobs.FlightSearchRoute)
                     .Returns(new[] { ProviderJobs.AeroDataBox });
            // NSubstitute has no Moq-style "DefaultValue.Empty": an unconfigured member returning
            // IReadOnlyCollection<T> answers null, not an empty collection. Every verification-key
            // test below reaches ProviderJobs.SupportedSourceNames, which enumerates this, so it
            // needs a non-null baseline even for tests that don't care what it contains.
            _registry.Sources.Returns(Array.Empty<IVerificationSource>());
            return new SetProviderRouteCommandHandler(
                _repository, _defaults, _registry, _adminContext, _audit, _time);
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
            // #531 fix round 1, extra finding: after Finding 1 moved the audit stage ahead of the
            // routing upsert, "no audit row on a refusal" is no longer guaranteed by statement
            // order alone (the audit stage happens to run after this check today, but nothing here
            // proves that stays true) -- it needs its own assertion.
            await _audit.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        }

        // #531 fix round 1, Finding 3: provider_routes.reason is varchar(512) with nothing
        // validating it beforehand, so an over-long reason reached Postgres as an unhandled
        // DbUpdateException -- a 500 naming nothing the caller could act on.
        [Fact]
        public async Task Handle_ReasonTooLong_Throws400()
        {
            var handler = Build();
            var reason = new string('x', SetProviderRouteCommandHandler.MaxReasonLength + 1);

            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroApi }, reason),
                default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains(SetProviderRouteCommandHandler.MaxReasonLength.ToString(), ex.Message);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<ProviderRoute>(), Arg.Any<CancellationToken>());
        }

        // #531 fix round 1, Finding 3: provider_routes.providers is varchar(256). Every individual
        // name here is a valid option for flight-search-route, so the options loop lets all 25
        // through -- only the joined-length check catches the overflow.
        [Fact]
        public async Task Handle_ProvidersJoinedTooLong_Throws400()
        {
            var handler = Build();
            var providers = Enumerable.Repeat(ProviderJobs.AeroDataBox, 25).ToArray();
            Assert.True(string.Join(',', providers).Length > SetProviderRouteCommandHandler.MaxProvidersLength);

            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, providers, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains(SetProviderRouteCommandHandler.MaxProvidersLength.ToString(), ex.Message);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<ProviderRoute>(), Arg.Any<CancellationToken>());
        }

        // #531 fix round 1, Finding 3: a short duplicate list (well under the 256-character joined
        // limit) isolates the duplicate-name check from the joined-length check above -- this input
        // would sail past a length check alone.
        [Fact]
        public async Task Handle_DuplicateProviders_Throws400()
        {
            var handler = Build();
            var providers = new[] { ProviderJobs.AeroDataBox, ProviderJobs.AeroDataBox };
            Assert.True(string.Join(',', providers).Length <= SetProviderRouteCommandHandler.MaxProvidersLength);

            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, providers, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains(ProviderJobs.AeroDataBox, ex.Message);
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

        [Fact]
        public async Task Handle_VerificationKeyNamingAnUnregisteredSource_Throws400()
        {
            // Configuration cannot refuse this: VerificationDefaultSources.Validate checks only the
            // join grammar, which is why a pre-#401 'aeroapi' default would have completed every
            // request straight to Error. A write is a request/response, so it can refuse.
            _registry.TryGet("nosuch").Returns((IVerificationSource?)null);

            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.VerificationAirport, new[] { "nosuch" }, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("nosuch", ex.Message);
        }

        [Fact]
        public async Task Handle_VerificationKeyNamingASourceThatDoesNotSupportTheEntityType_Throws400()
        {
            var countryOnly = Substitute.For<IVerificationSource>();
            countryOnly.Name.Returns("mledoze");
            countryOnly.SupportedEntityTypes.Returns(new[] { VerificationEntityType.Country });
            _registry.TryGet("mledoze").Returns(countryOnly);

            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.VerificationAirport, new[] { "mledoze" }, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("Airport", ex.Message);
        }

        [Fact]
        public async Task Handle_VerificationKeyNamingASupportingSource_Upserts()
        {
            var airports = Substitute.For<IVerificationSource>();
            airports.Name.Returns("ourairports");
            airports.SupportedEntityTypes.Returns(new[] { VerificationEntityType.Airport });
            _registry.TryGet("ourairports").Returns(airports);
            _registry.Sources.Returns(new[] { airports });

            await Build().Handle(new SetProviderRouteCommand(
                ProviderJobs.VerificationAirport, new[] { "ourairports" }, null), default);

            await _repository.Received(1).UpsertAsync(
                Arg.Is<ProviderRoute>(row => row.Providers == "ourairports"),
                Arg.Any<CancellationToken>());
        }
    }
}
