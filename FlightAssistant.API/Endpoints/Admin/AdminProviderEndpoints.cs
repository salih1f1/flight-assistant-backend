using FlightAssistant.Application.Commands.Admin.Providers;
using FlightAssistant.Application.Queries.Admin.Providers;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    /// <summary>
    /// <c>GET</c>/<c>PUT</c> for provider routing (#531). Mapped onto the shared <c>/admin</c> group,
    /// which already carries <c>RequireAuthorization("AdminOnly")</c> and
    /// <c>RequireCors("AdminPanel")</c> — neither is re-applied here.
    /// </summary>
    public static class AdminProviderEndpoints
    {
        public static void MapAdminProviderEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("providers").WithTags("Admin - Providers");

            // GET /admin/providers
            group.MapGet("", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetProviderRoutesQuery(), ct)))
            .WithName("AdminGetProviderRoutes")
            .WithSummary("Which provider serves each routing decision")
            .WithDescription(
                "Every routing decision an admin can change, its current providers, and whether the " +
                "value came from an operator or from configuration. 'canChange' is false when this " +
                "environment configures no alternative — for route search, when AeroDataBox:ApiKey is " +
                "unset — and configuration is a hard ceiling no change can lift. Also returns the full " +
                "call map, including the operations whose provider is fixed and why, and each " +
                "provider's remaining headroom. AeroDataBox quota is null until this instance has " +
                "called it — unknown, not exhausted — and is per instance. List prices are authored " +
                "and carry their own date; neither provider publishes a price list through an API.")
            .Produces<AdminProviderMapDto>(200);

            // PUT /admin/providers/{job}
            group.MapPut("{job}", async (
                string job,
                SetProviderRouteRequest request,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(
                    new SetProviderRouteCommand(job, request.Providers, request.Reason), ct);
                return Results.Ok(result);
            })
            .WithName("AdminSetProviderRoute")
            .WithSummary("Choose which provider serves a routing decision")
            .WithDescription(
                "Takes effect within the 10-second read cache for searches not already cached. " +
                "Cached search results are unaffected and are not wrong: the search cache key carries " +
                "the provider, so the two providers never serve each other's rows. A verification key " +
                "affects newly queued requests and track-time provisioning only -- the job uses each " +
                "request's own stored sources, so requests already in the queue keep the sources they " +
                "were queued with. Audited. 400 when a provider is not an option for this key, when " +
                "more than one is named for a key that honours only one (route search: a verification " +
                "key does take an ordered list), or when a value is longer than its column allows; " +
                "404 when the key is unknown; 409 when this environment configures no alternative.")
            .Produces<AdminProviderRouteDto>(200)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        }
    }
}
