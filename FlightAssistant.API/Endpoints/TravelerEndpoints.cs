using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Queries.Travelers;
using FlightAssistant.Contracts.Requests.Travelers;
using FlightAssistant.Contracts.Responses.Travelers;
using System.Collections.Generic;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints
{
    public static class TravelerEndpoints
    {
        public static void MapTravelerEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("travelers").RequireAuthorization("AccessAsUser").WithTags("Travelers");

            // POST /travelers — register (idempotent, called on every login).
            // Every field it stores comes from the validated token, never from the request: this
            // route takes no body at all (#544). It used to read Email and DisplayName from a
            // RegisterTravelerRequest, so a traveler could write an address that appears nowhere in
            // their own token — while the handler, PATCH /travelers/me and this endpoint's own
            // description all already described Azure as the source of truth for both.
            group.MapPost("", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                var command = new RegisterTravelerCommand(
                    http.User.GetTravelerId(),
                    http.User.GetEmail(),
                    http.User.GetDisplayName());

                var newTravelerId = await mediator.Send(command, ct);
                return Results.Created("/travelers/me", new RegisterTravelerResponse(newTravelerId));
            })
            .WithName("RegisterTraveler")
            .WithSummary("Register traveler")
            .WithDescription(
                "Creates a traveler profile linked to the authenticated Azure AD B2C identity. " +
                "Takes no request body: the email and display name are read from the sign-in token " +
                "(the 'email', 'preferred_username' or 'emails' claim, and 'name'), so they always " +
                "match the authenticated identity. " +
                "Idempotent — safe to call on every login, and each call re-reads those claims, " +
                "which is the only thing that refreshes them locally when they change in Azure. " +
                "A token carrying no email claim at all is refused as unauthenticated; one whose " +
                "claim is not an address — a phone sign-in, say — is a 400.")
            .Produces<RegisterTravelerResponse>(201)
            .ProducesValidationProblem();

            // GET /travelers/me — get own profile
            group.MapGet("me", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                var travelerId = http.User.GetTravelerId();
                var result = await mediator.Send(new GetTravelerProfileQuery(travelerId), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new TravelerDto(result.Id, result.Email, result.DisplayName));
            })
            .WithName("GetTravelerProfile")
            .WithSummary("Get own profile")
            .WithDescription(
                "Returns the authenticated traveler's profile. Display name is owned by " +
                "Azure AD B2C and is refreshed from the sign-in token on each login; it is read-only here.")
            .Produces<TravelerDto>(200)
            .ProducesProblem(404);

            // POST /travelers/me/device-tokens — register a push notification token
            group.MapPost("me/device-tokens", async (HttpContext http, [FromBody] AddDeviceTokenRequest req, IMediator mediator, CancellationToken ct) =>
            {
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
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new RemoveDeviceTokenCommand(travelerId, deviceTokenId), ct);
                return Results.NoContent();
            })
            .WithName("RemoveDeviceToken")
            .WithSummary("Remove device token")
            .WithDescription(
                "Removes a previously registered push notification device token. " +
                "Use this when a user logs out or when the OS invalidates the token. " +
                "Route parameter: deviceTokenId — the ID of the device token to remove. " +
                "Idempotent in the token: removing one that is already gone returns 204. " +
                "The 404 means the traveler is not registered — call POST /travelers first. ")
            .Produces(204)
            .ProducesProblem(404);
        }

    }
}
