using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Queries.Travelers;
using FlightAssistant.Contracts.Requests.Travelers;
using FlightAssistant.Contracts.Responses.Travelers;
using System.Collections.Generic;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace FlightAssistant.API.Endpoints
{
    public static class TravelerEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapTravelerEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("travelers").RequireAuthorization().WithTags("Travelers");

            // POST /travelers — register (idempotent, called on every login)
            // Takes no body: identity (email, name, country) is read from the verified token,
            // never from client input, so Azure AD B2C stays the single source of truth.
            group.MapPost("", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var newTravelerId = await mediator.Send(new RegisterTravelerCommand(
                    travelerId,
                    http.User.GetEmail() ?? string.Empty,
                    http.User.GetDisplayName(),
                    http.User.GetCountryCode()), ct);
                return Results.Created("/travelers/me", new RegisterTravelerResponse(newTravelerId));
            })
            .WithName("RegisterTraveler")
            .WithSummary("Register traveler")
            .WithDescription(
                "Creates a traveler profile linked to the authenticated Azure AD B2C identity. " +
                "Takes no request body — email, display name, and country are derived from the sign-in token. " +
                "Idempotent — safe to call on every login; reconciles the profile from the token and does " +
                "nothing if the traveler already exists and is unchanged.")
            .Produces<RegisterTravelerResponse>(201)
            .ProducesValidationProblem();

            // GET /travelers/me — get own profile
            group.MapGet("me", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var result = await mediator.Send(new GetTravelerProfileQuery(travelerId), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new TravelerDto(result.Id, result.Email, result.DisplayName, result.CountryCode));
            })
            .WithName("GetTravelerProfile")
            .WithSummary("Get own profile")
            .WithDescription(
                "Returns the authenticated traveler's profile. Display name and country are owned by " +
                "Azure AD B2C and are refreshed from the sign-in token on each login; they are read-only here.")
            .Produces<TravelerDto>(200)
            .ProducesProblem(404);

            // POST /travelers/me/device-tokens — register a push notification token
            group.MapPost("me/device-tokens", async (HttpContext http, [FromBody] AddDeviceTokenRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new AddDeviceTokenCommand(travelerId, req.Platform, req.Token), ct);
                return Results.NoContent();
            })
            .WithName("AddDeviceToken")
            .WithSummary("Register device token")
            .WithDescription(
                "Registers a push notification device token for the authenticated traveler. " +
                "Currently supports iOS (APNs). The token is used to send flight status alerts when notifications are enabled for a tracked flight.")
            .Produces(204)
            .ProducesValidationProblem();

            // GET /travelers/me/device-tokens — list registered push tokens
            group.MapGet("me/device-tokens", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var tokens = await mediator.Send(new GetDeviceTokensQuery(travelerId), ct);
                var dtos = tokens.Select(t => new DeviceTokenDto(t.Id, t.Platform, t.Token, t.LastSeenUtc)).ToList();
                return Results.Ok(dtos);
            })
            .WithName("GetDeviceTokens")
            .WithSummary("List registered device tokens")
            .WithDescription("Returns all push notification device tokens registered for the authenticated traveler.")
            .Produces<List<DeviceTokenDto>>(200);

            // DELETE /travelers/me/device-tokens/{deviceTokenId}
            group.MapDelete("me/device-tokens/{deviceTokenId:guid}", async (HttpContext http, Guid deviceTokenId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new RemoveDeviceTokenCommand(travelerId, deviceTokenId), ct);
                return Results.NoContent();
            })
            .WithName("RemoveDeviceToken")
            .WithSummary("Remove device token")
            .WithDescription(
                "Removes a previously registered push notification device token. " +
                "Use this when a user logs out or when the OS invalidates the token. " +
                "Route parameter: deviceTokenId — the ID of the device token to remove.")
            .Produces(204)
            .ProducesProblem(404);
        }

    }
}
