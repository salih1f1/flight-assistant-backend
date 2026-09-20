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

        // Whole-branch review: flight-search-route's reader, FlightSearchRouter, takes providers[0]
        // and discards the rest -- so ["aeroapi","aerodatabox"] used to pass every check here, store
        // as "aeroapi,aerodatabox", and have GET /admin/providers echo the whole list back as the
        // current routing while only the first name was ever in force. Not a length or duplicate
        // problem: this list is short and its names are distinct, so only a cardinality check catches
        // it.
        [Fact]
        public async Task Handle_SingleChoiceKeyNamingMoreThanOneProvider_Throws400()
        {
            var handler = Build();
            var providers = new[] { ProviderJobs.AeroApi, ProviderJobs.AeroDataBox };

            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, providers, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("exactly one provider", ex.Message);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<ProviderRoute>(), Arg.Any<CancellationToken>());
            await _audit.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        }

        // The companion, so the refusal above cannot quietly grow to cover every key: a verification
        // key genuinely takes an ordered list -- ReferenceDataProvisioner asks each source in turn
        // until one answers -- and must keep accepting one.
        [Fact]
        public async Task Handle_VerificationKeyNamingTwoSupportingSources_Upserts()
        {
            var ourairports = Substitute.For<IVerificationSource>();
            ourairports.Name.Returns("ourairports");
            ourairports.SupportedEntityTypes.Returns(new[] { VerificationEntityType.Airport });
            var aeroapi = Substitute.For<IVerificationSource>();
            aeroapi.Name.Returns("aeroapi");
            aeroapi.SupportedEntityTypes.Returns(new[] { VerificationEntityType.Airport });
            _registry.TryGet("ourairports").Returns(ourairports);
            _registry.TryGet("aeroapi").Returns(aeroapi);
            _registry.Sources.Returns(new[] { ourairports, aeroapi });

            var result = await Build().Handle(new SetProviderRouteCommand(
                ProviderJobs.VerificationAirport, new[] { "ourairports", "aeroapi" }, null), default);

            await _repository.Received(1).UpsertAsync(
                Arg.Is<ProviderRoute>(row => row.Providers == "ourairports,aeroapi"),
                Arg.Any<CancellationToken>());
            Assert.Equal(new[] { "ourairports", "aeroapi" }, result.Providers);
        }

        // Whole-branch review: provider_routes.changed_by is varchar(256) and took
        // IAdminContext.ActorName ?? ActorId straight from a claim with no check -- the one column of
        // the three left unbounded, so an over-long display name reached Postgres as an unhandled
        // DbUpdateException (a 500), invisible on the API suite's InMemory provider.
        [Fact]
        public async Task Handle_ActingAdminsNameTooLong_Throws400()
        {
            var handler = Build();
            // After Build(), which sets the ordinary "Ada" baseline this must override.
            _adminContext.ActorName.Returns(
                new string('n', SetProviderRouteCommandHandler.MaxChangedByLength + 1));

            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroApi }, null),
                default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains(SetProviderRouteCommandHandler.MaxChangedByLength.ToString(), ex.Message);
            await _repository.DidNotReceive().UpsertAsync(Arg.Any<ProviderRoute>(), Arg.Any<CancellationToken>());
            // Refused before StageAuditAsync, so the audit row is not staged either -- the audit row
            // carries the same string in AdminAuditLog.ActorName.
            await _audit.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Whole-branch review: the audit row is staged <em>before</em> the routing upsert so the
        /// upsert's own <c>SaveChangesAsync</c> commits both in one transaction (see
        /// <c>StageAuditAsync</c>'s remarks). Moving the stage back below the upsert -- its original
        /// shape -- reopens the two-transaction defect with the whole suite green, because every other
        /// test here asserts only that both calls happened. This pins the order itself.
        /// </summary>
        [Fact]
        public async Task Handle_StagesTheAuditRowBeforeTheRoutingUpsert()
        {
            var handler = Build();

            await handler.Handle(new SetProviderRouteCommand(
                ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroApi }, "quota gone"), default);

            Received.InOrder(() =>
            {
                _audit.AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
                _repository.UpsertAsync(Arg.Any<ProviderRoute>(), Arg.Any<CancellationToken>());
            });
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
            // Identical here by construction, not coincidence: the 409 ceiling check above already
            // refused a key whose configuration overrides the stored row, so nothing can override
            // what was just written. GET /admin/providers is where the two can differ.
            Assert.Equal(new[] { ProviderJobs.AeroApi }, result.EffectiveProviders);
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
