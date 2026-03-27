using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Queries.Travelers;
using FlightAssistant.Contracts.Requests.Travelers;
using FlightAssistant.Contracts.Responses.Travelers;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using System.Security.Claims;

namespace FlightAssistant.API.Endpoints
{
    public static class TravelerEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapTravelerEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("travelers").RequireAuthorization();

            // POST /travelers — register (idempotent, called on first login)
            group.MapPost("", async (HttpContext http, [FromBody] RegisterTravelerRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new RegisterTravelerCommand(travelerId, req.Email, req.DisplayName, req.CountryCode), ct);
                return Results.Created($"/travelers/me", null);
            });

            // GET /travelers/me — get own profile
            group.MapGet("me", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                var result = await mediator.Send(new GetTravelerProfileQuery(travelerId), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new TravelerDto(result.Id, result.Email, result.DisplayName, result.CountryCode, result.NationalityCountryCode, result.ResidenceCountryCode));
            });

            // PATCH /travelers/me — update profile
            group.MapPatch("me", async (HttpContext http, [FromBody] UpdateTravelerProfileRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new UpdateTravelerProfileCommand(travelerId, req.DisplayName, req.CountryCode, req.NationalityCountryCode, req.ResidenceCountryCode), ct);
                return Results.NoContent();
            });

            // POST /travelers/me/device-tokens — register a push notification token
            group.MapPost("me/device-tokens", async (HttpContext http, [FromBody] AddDeviceTokenRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new AddDeviceTokenCommand(travelerId, req.Platform, req.Token), ct);
                return Results.NoContent();
            });

            // DELETE /travelers/me/device-tokens/{deviceTokenId}
            group.MapDelete("me/device-tokens/{deviceTokenId:guid}", async (HttpContext http, Guid deviceTokenId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new RemoveDeviceTokenCommand(travelerId, deviceTokenId), ct);
                return Results.NoContent();
            });
        }

        private static Guid GetTravelerId(HttpContext http)
        {
            var oidClaim = http.User.FindFirstValue("oid") ?? http.User.FindFirstValue("sub")
                ?? throw new InvalidOperationException("User OID claim missing.");
            return Guid.Parse(oidClaim);
        }
    }
}
