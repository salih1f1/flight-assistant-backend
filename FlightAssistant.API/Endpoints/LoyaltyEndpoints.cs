using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Queries.Loyalty;
using FlightAssistant.Contracts.Requests.Loyalty;
using FlightAssistant.Contracts.Responses.Loyalty;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using System.Security.Claims;

namespace FlightAssistant.API.Endpoints
{
    public static class LoyaltyEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapLoyaltyEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("loyalty").RequireAuthorization();

            // GET /loyalty/programs — all programs with tiers (reference data)
            group.MapGet("programs", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var programs = await mediator.Send(new GetLoyaltyProgramsQuery(), ct);
                var dtos = programs.Select(p => new LoyaltyProgramDto(
                    p.Id, p.Code, p.Name, p.Kind, p.AirlineId, p.AllianceCode,
                    p.Tiers.Select(t => new LoyaltyTierDto(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList()));
                return Results.Ok(dtos);
            });

            // GET /loyalty/memberships — traveler's own memberships
            group.MapGet("memberships", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                var memberships = await mediator.Send(new GetTravelerMembershipsQuery(travelerId), ct);
                var dtos = memberships.Select(m => new LoyaltyMembershipDto(m.Id, m.ProgramId, m.AccountNumber, m.TierId, m.TierExpiryDate, m.PointsExpiryDate));
                return Results.Ok(dtos);
            });

            // POST /loyalty/memberships — add a membership
            group.MapPost("memberships", async (HttpContext http, [FromBody] AddMembershipRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                var id = await mediator.Send(new AddLoyaltyMembershipCommand(travelerId, req.ProgramId, req.AccountNumber, req.TierId, req.TierExpiryDate, req.PointsExpiryDate), ct);
                return Results.Created($"/loyalty/memberships/{id}", new { id });
            });

            // PUT /loyalty/memberships/{id} — update a membership
            group.MapPut("memberships/{id:guid}", async (HttpContext http, Guid id, [FromBody] AddMembershipRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new UpdateLoyaltyMembershipCommand(travelerId, id, req.AccountNumber, req.TierId, req.TierExpiryDate, req.PointsExpiryDate), ct);
                return Results.NoContent();
            });

            // DELETE /loyalty/memberships/{id}
            group.MapDelete("memberships/{id:guid}", async (HttpContext http, Guid id, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new RemoveLoyaltyMembershipCommand(travelerId, id), ct);
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
