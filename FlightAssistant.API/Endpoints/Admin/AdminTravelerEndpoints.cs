using FlightAssistant.Application.Commands.Admin.Travelers;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Queries.Admin.Travelers;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Flights;
using FlightAssistant.Contracts.Requests.Loyalty;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminTravelerEndpoints
    {
        public static void MapAdminTravelerEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("travelers")
                .WithTags("Admin - Travelers");

            // GET /admin/travelers/{id}/detail — the traveler with device tokens, tracked flights
            // and loyalty memberships in one call. A separate route from GET /admin/travelers/{id}
            // (below) rather than an extension of it, so the two response shapes can stay distinct:
            // the narrow list/by-id row versus the full relationship drill-down. Both mask Email the
            // same way (#321) — this route additionally masks Token and AccountNumber, which the
            // narrow shape below never returns at all.
            group.MapGet("{id:guid}/detail", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminTravelerDetailQuery(id), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new AdminTravelerDetailDto(
                    result.Id, result.Email, result.DisplayName, result.CreatedUtc,
                    result.DeviceTokens.Select(t => new AdminDeviceTokenDto(t.Id, t.Platform, t.Token, t.LastSeenUtc)).ToList(),
                    result.TrackedFlights.Select(f => new AdminTrackedFlightDto(
                        f.TrackingId, f.FlightId, f.FlightNumber, f.AirlineId,
                        f.DepartureAirportId, f.ArrivalAirportId, f.ServiceDate, f.Notify)).ToList(),
                    result.TrackedFlightsTruncated,
                    result.LoyaltyMemberships.Select(m => new AdminMembershipDto(
                        m.Id, m.ProgramId, m.AccountNumber, m.TierId, m.TierExpiryDate)).ToList()));
            })
            .WithName("AdminGetTravelerDetail")
            .WithSummary("Get traveler detail")
            .WithDescription(
                "Returns the traveler together with its device tokens, tracked flights and loyalty " +
                "memberships in one call. Email and every device token are masked as \"<redacted>\"; " +
                "reveal the real value via POST /admin/data/{table}/{id}/reveal. TrackedFlights is " +
                $"capped at {GetAdminTravelerDetailQueryHandler.MaxTrackedFlights}, newest first; " +
                "TrackedFlightsTruncated is true when the cap actually cut rows off, and the full " +
                "relationship is available via GET /admin/data/traveler-flights. Device tokens and " +
                "loyalty memberships are not capped.")
            .Produces<AdminTravelerDetailDto>(200)
            .ProducesProblem(404);

            // GET /admin/travelers
            group.MapGet("", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAllTravelersQuery(paging), ct);
                var dtos = result.Items.Select(t => new AdminTravelerDto(
                    t.Id, t.Email, t.DisplayName, t.CreatedUtc)).ToList();
                return Results.Ok(new PagedResult<AdminTravelerDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAllTravelers")
            .WithSummary("List all travelers")
            .WithDescription(
                "Returns every traveler, paged. Email is masked as \"<redacted>\"; reveal the real " +
                "value via POST /admin/data/travelers/{id}/reveal, which audits the read. This is " +
                "the intended posture, not a gap to be fixed back.")
            .Produces<PagedResult<AdminTravelerDto>>(200)
            .ProducesValidationProblem();

            // GET /admin/travelers/{id}
            group.MapGet("{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminTravelerByIdQuery(id), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new AdminTravelerDto(
                    result.Id, result.Email, result.DisplayName, result.CreatedUtc));
            })
            .WithName("AdminGetTravelerById")
            .WithSummary("Get traveler by ID")
            .WithDescription(
                "Returns one traveler. Email is masked as \"<redacted>\"; reveal the real value via " +
                "POST /admin/data/travelers/{id}/reveal, which audits the read. This is the intended " +
                "posture, not a gap to be fixed back.")
            .Produces<AdminTravelerDto>(200)
            .ProducesProblem(404);

            // DELETE /admin/travelers/{id}
            group.MapDelete("{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteTravelerCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteTraveler")
            .WithSummary("Delete traveler")
            .Produces(204)
            .ProducesValidationProblem();

            // POST /admin/travelers/{id}/flights — track a flight for this traveler (#340), so an
            // operator can test tracking for a traveler without that traveler's own sign-in.
            group.MapPost("{id:guid}/flights", async (Guid id, [FromBody] TrackFlightRequest req, IMediator mediator, CancellationToken ct) =>
            {
                // TrackFlightCommand trusts its TravelerId: POST /flights takes it from the caller's
                // own token. Here it comes from the URL, and no foreign key on traveler_flights
                // would stop a tracking row for a traveler who doesn't exist.
                if (await mediator.Send(new GetAdminTravelerByIdQuery(id), ct) is null) return Results.NotFound();

                var flightId = await mediator.Send(new TrackFlightCommand(
                    id,
                    req.FlightNumber,
                    req.ServiceDate,
                    req.DepartureAirportIata,
                    req.ArrivalAirportIata,
                    req.ScheduledDepartureUtc,
                    req.ScheduledArrivalUtc), ct);

                return Results.Created($"/admin/flights/{flightId}", new { flightId });
            })
            .WithName("AdminTrackFlightForTraveler")
            .WithSummary("Track a flight for a traveler")
            .WithDescription(
                "Adds a flight to this traveler's tracking list, exactly as POST /flights does for the " +
                "signed-in traveler. A row from GET /admin/flights/search can be posted here unmodified. " +
                "Tracking a flight the traveler already tracks changes nothing and still returns 201 with " +
                "its flightId. An unknown traveler is a 404 and writes nothing. " +
                "The same cap as POST /flights applies: at the traveler's limit of flights tracked at a time, tracking another returns 409 and writes nothing. " +
                "If the airline or either airport is not already known to the system, it is looked up in the external reference sources and created automatically (#465). " +
                "So 404 covers two things here: an unknown traveler, and a source answering that no such airline, airport or country exists — the country case names one the " +
                "reference data itself requires (a CountryCode foreign key), never one the caller sent. " +
                "503 means that lookup could not be completed — a source was unreachable, the flight data budget is spent, or it took longer than the configured budget; retry after the period in the Retry-After header.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409)
            .ProducesProblem(503);

            // POST /admin/travelers/{id}/memberships — add a loyalty membership for this traveler
            // (#475), so an operator can set one up for a traveler without that traveler's own
            // sign-in. The sibling of the track-flight route above, and the same shape.
            group.MapPost("{id:guid}/memberships", async (Guid id, [FromBody] AddMembershipRequest req, IMediator mediator, CancellationToken ct) =>
            {
                // AddLoyaltyMembershipCommand trusts its TravelerId: POST /loyalty/memberships takes
                // it from the caller's own token. Here it comes from the URL, and no foreign key on
                // traveler_loyalty_memberships would stop a membership for a traveler who doesn't
                // exist — the same reason the track-flight route checks first.
                if (await mediator.Send(new GetAdminTravelerByIdQuery(id), ct) is null) return Results.NotFound();

                var membershipId = await mediator.Send(new AddLoyaltyMembershipCommand(
                    id,
                    req.ProgramId,
                    req.AccountNumber,
                    req.TierId,
                    req.TierExpiryDate), ct);

                return Results.Created($"/admin/travelers/{id}/detail", new { id = membershipId });
            })
            .WithName("AdminAddMembershipForTraveler")
            .WithSummary("Add a loyalty membership for a traveler")
            .WithDescription(
                "Links a loyalty program membership to this traveler, exactly as POST /loyalty/memberships does " +
                "for the signed-in traveler. Body fields: ProgramId (from GET /admin/loyalty/programs), " +
                "AccountNumber, TierId (optional, from that program's tier list), TierExpiryDate (optional, " +
                "YYYY-MM-DD, today or later). An unknown traveler is a 404 and writes " +
                "nothing. A ProgramId we don't have, or a TierId belonging to another program, is a 409 and " +
                "writes nothing. Adding a membership the traveler already has is not refused: the same program " +
                "can be added twice, as it can through POST /loyalty/memberships.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);

            // DELETE /admin/travelers/{travelerId}/device-tokens/{tokenId}
            group.MapDelete("{travelerId:guid}/device-tokens/{tokenId:guid}",
                async (Guid travelerId, Guid tokenId, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteDeviceTokenCommand(travelerId, tokenId), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteDeviceToken")
            .WithSummary("Delete device token")
            .WithDescription(
                "Removes a stale APNs device token. This is NOT a device revocation: it only stops " +
                "push notifications from reaching that token, and the client re-registers a fresh " +
                "one on its next launch.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404);
        }
    }
}
