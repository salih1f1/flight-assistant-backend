using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Queries.Visa;
using FlightAssistant.Domain.Common.Exceptions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
// Alias the two VisaRequirementResult types to avoid ambiguity:
// the provider DTO (6 params, includes RawPayloadJson) vs the query result DTO (5 params)
using IVisaRequirementsProvider = FlightAssistant.Application.Interfaces.ExternalServices.IVisaRequirementsProvider;
using ProviderVisaResult = FlightAssistant.Application.Interfaces.ExternalServices.VisaRequirementResult;

namespace FlightAssistant.Application.Tests.Queries.Visa;

public class GetVisaRequirementsQueryHandlerTests
{
    private readonly IVisaRequirementsProvider _provider = Substitute.For<IVisaRequirementsProvider>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly GetVisaRequirementsQueryHandler _handler;

    private static readonly ProviderVisaResult SampleResult = new(
        "GB", "JP", "visa_free", 90, null, null);

    public GetVisaRequirementsQueryHandlerTests()
    {
        _handler = new GetVisaRequirementsQueryHandler(_provider, _cache);
    }

    [Fact]
    public async Task Handle_ProviderReturnsResult_CachesAndReturns()
    {
        _cache.GetAsync<VisaRequirementResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((VisaRequirementResult?)null);
        _provider.GetRequirementsAsync("GB", "JP", Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        var result = await _handler.Handle(new GetVisaRequirementsQuery("GB", "JP"), default);

        Assert.Equal("GB", result.PassportCountryCode);
        Assert.Equal("JP", result.DestinationCountryCode);
        Assert.Equal("visa_free", result.AdmissionType);
        await _cache.Received(1).SetAsync(
            Arg.Any<string>(), Arg.Any<VisaRequirementResult>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CacheHit_DoesNotCallProvider()
    {
        _cache.GetAsync<VisaRequirementResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisaRequirementResult("GB", "JP", "visa_free", 90, null));

        var result = await _handler.Handle(new GetVisaRequirementsQuery("GB", "JP"), default);

        Assert.Equal("visa_free", result.AdmissionType);
        await _provider.DidNotReceive().GetRequirementsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ProviderReturnsNull_Throws503()
    {
        _cache.GetAsync<VisaRequirementResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((VisaRequirementResult?)null);
        _provider.GetRequirementsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProviderVisaResult?)null);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(new GetVisaRequirementsQuery("GB", "JP"), default));

        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task Handle_ProviderThrows503_PropagatesException()
    {
        _cache.GetAsync<VisaRequirementResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((VisaRequirementResult?)null);
        _provider.GetRequirementsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("Visa requirements service is temporarily unavailable.", 503));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(new GetVisaRequirementsQuery("GB", "JP"), default));

        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task Handle_NoCacheWrite_WhenProviderThrows()
    {
        _cache.GetAsync<VisaRequirementResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((VisaRequirementResult?)null);
        _provider.GetRequirementsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("unavailable", 503));

        await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(new GetVisaRequirementsQuery("GB", "JP"), default));

        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<VisaRequirementResult>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }
}
