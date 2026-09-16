using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Queries.Loyalty;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Loyalty;
using FlightAssistant.Contracts.Responses.Loyalty;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using System.Collections.Generic;

namespace FlightAssistant.API.Endpoints
{
    public static class LoyaltyEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapLoyaltyEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("loyalty").RequireAuthorization().WithTags("Loyalty");

            // GET /loyalty/programs/search?airlineIata=BA — filter programs by airline
            group.MapGet("programs/search", async (HttpContext http, string airlineIata, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var results = await mediator.Send(new SearchLoyaltyProgramsQuery(airlineIata), ct);
                var dtos = results.Select(p => new LoyaltyProgramDto(
                    p.Id, p.Code, p.Name, p.Kind, p.AirlineId, p.AllianceCode,
                    p.Tiers.Select(t => new LoyaltyTierDto(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList())).ToList();
                return Results.Ok(dtos);
            })
            .WithName("SearchLoyaltyPrograms")
            .WithSummary("Search loyalty programs by airline")
            .WithDescription(
                "Returns loyalty programs linked to the specified airline IATA code (e.g. 'BA'). " +
                "Use this to populate the program picker when the user knows their airline.")
            .Produces<List<LoyaltyProgramDto>>(200)
            .ProducesValidationProblem()
            .RequireRateLimiting("loyalty-programs");

            // GET /loyalty/programs — paginated programs with tiers (reference data)
            group.MapGet("programs", async (HttpContext http, [AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var result = await mediator.Send(new GetLoyaltyProgramsQuery(paging), ct);
                var dtos = result.Items.Select(p => new LoyaltyProgramDto(
                    p.Id, p.Code, p.Name, p.Kind, p.AirlineId, p.AllianceCode,
                    p.Tiers.Select(t => new LoyaltyTierDto(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList())).ToList();
                return Results.Ok(new PagedResult<LoyaltyProgramDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("GetLoyaltyPrograms")
            .WithSummary("List loyalty programs")
            .WithDescription(
                "Returns a paginated list of airline loyalty programs, including their full tier hierarchy. " +
                "Use this to populate program/tier picker UIs. Supports ?page and ?pageSize (default 50, max 200).")
            .Produces<PagedResult<LoyaltyProgramDto>>(200)
            .ProducesValidationProblem()
            .RequireRateLimiting("loyalty-programs");

            // GET /loyalty/memberships — traveler's own memberships (paginated)
            group.MapGet("memberships", async (HttpContext http, [AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var result = await mediator.Send(new GetTravelerMembershipsQuery(travelerId, paging), ct);
                var dtos = result.Items.Select(m => new LoyaltyMembershipDto(m.Id, m.ProgramId, m.AccountNumber, m.TierId, m.TierExpiryDate, m.PointsExpiryDate)).ToList();
                return Results.Ok(new PagedResult<LoyaltyMembershipDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("GetMemberships")
            .WithSummary("List own memberships")
            .WithDescription("Returns a paginated list of the authenticated traveler's loyalty memberships. Supports ?page and ?pageSize (default 50, max 200).")
            .Produces<PagedResult<LoyaltyMembershipDto>>(200)
            .ProducesValidationProblem();

            // POST /loyalty/memberships — add a membership
            group.MapPost("memberships", async (HttpContext http, [FromBody] AddMembershipRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var id = await mediator.Send(new AddLoyaltyMembershipCommand(travelerId, req.ProgramId, req.AccountNumber, req.TierId, req.TierExpiryDate, req.PointsExpiryDate), ct);
                return Results.Created($"/loyalty/memberships/{id}", new { id });
            })
            .WithName("AddMembership")
            .WithSummary("Add a loyalty membership")
            .WithDescription(
                "Links a loyalty program membership to the authenticated traveler. " +
                "Body fields: ProgramId (from GET /loyalty/programs), AccountNumber, " +
                "TierId (optional, from the program's tier list), TierExpiryDate (optional, YYYY-MM-DD), PointsExpiryDate (optional, YYYY-MM-DD).")
            .Produces(201)
            .ProducesValidationProblem();

            // PUT /loyalty/memberships/{id} — update a membership
            group.MapPut("memberships/{id:guid}", async (HttpContext http, Guid id, [FromBody] UpdateMembershipRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new UpdateLoyaltyMembershipCommand(travelerId, id, req.AccountNumber, req.TierId, req.TierExpiryDate, req.PointsExpiryDate), ct);
                return Results.NoContent();
            })
            .WithName("UpdateMembership")
            .WithSummary("Update a loyalty membership")
            .WithDescription(
                "Replaces the account number, tier, and expiry dates for the specified membership. " +
                "Route parameter: id — the membership ID returned when it was added. " +
                "Only the authenticated traveler's own memberships can be updated. " +
                "Returns 404 if no membership with that id exists, and 403 if it belongs to another traveler.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(403)
            .ProducesProblem(404);

            // DELETE /loyalty/memberships/{id}
            group.MapDelete("memberships/{id:guid}", async (HttpContext http, Guid id, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new RemoveLoyaltyMembershipCommand(travelerId, id), ct);
                return Results.NoContent();
            })
            .WithName("RemoveMembership")
            .WithSummary("Remove a loyalty membership")
            .WithDescription(
                "Deletes a loyalty membership from the authenticated traveler's profile. " +
                "Route parameter: id — the membership ID to remove. " +
                "Idempotent: removing a membership that does not exist also returns 204, so there is no 404. " +
                "Returns 403 if the membership belongs to another traveler.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(403);
        }

    }
}
