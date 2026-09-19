using FlightAssistant.Application.Commands.Admin.Loyalty;
using FlightAssistant.Application.Queries.Loyalty;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin.Loyalty;
using FlightAssistant.Contracts.Responses.Loyalty;
using MediatR;
using Microsoft.AspNetCore.Mvc;

// LoyaltyProgramResult and LoyaltyTierResult are defined in FlightAssistant.Application.Queries.Loyalty

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminLoyaltyEndpoints
    {
        public static void MapAdminLoyaltyEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("loyalty")
                .WithTags("Admin - Loyalty");

            // GET /admin/loyalty/programs — reuses existing paginated query
            group.MapGet("programs", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetLoyaltyProgramsQuery(paging), ct);
                var dtos = result.Items.Select(p => new LoyaltyProgramDto(
                    p.Id, p.Code, p.Name, p.Kind, p.AirlineId,
                    p.Tiers.Select(t => new LoyaltyTierDto(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode))
                           .ToList())).ToList();
                return Results.Ok(new PagedResult<LoyaltyProgramDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetLoyaltyPrograms")
            .WithSummary("List all loyalty programs")
            .Produces<PagedResult<LoyaltyProgramDto>>(200)
            .ProducesValidationProblem();

            // POST /admin/loyalty/programs
            group.MapPost("programs", async ([FromBody] CreateLoyaltyProgramRequest req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(new CreateLoyaltyProgramCommand(req.Code, req.Name, req.KindId, req.AirlineId), ct);
                return Results.Created($"/admin/loyalty/programs/{id}", new { id });
            })
            .WithName("AdminCreateLoyaltyProgram")
            .WithSummary("Create loyalty program")
            .WithDescription("409 when KindId or AirlineId names a row that does not exist.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);

            // PUT /admin/loyalty/programs/{id}
            group.MapPut("programs/{id:int}", async (int id, [FromBody] UpdateLoyaltyProgramRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateLoyaltyProgramCommand(id, req.Code, req.Name, req.KindId, req.AirlineId), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateLoyaltyProgram")
            .WithSummary("Update loyalty program")
            .WithDescription("409 when KindId or AirlineId names a row that does not exist.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);

            // DELETE /admin/loyalty/programs/{id}
            group.MapDelete("programs/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteLoyaltyProgramCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteLoyaltyProgram")
            .WithSummary("Delete loyalty program")
            .Produces(204)
            .ProducesValidationProblem();

            MapTierEndpoints(group);
        }

        /// <summary>
        /// Writing a program's tiers (#488). A tier is always addressed through the program that
        /// owns it, which is what stops one program's route from reaching another program's tier:
        /// the handlers look the tier up inside the program, so anything else is a 404.
        /// </summary>
        private static void MapTierEndpoints(RouteGroupBuilder group)
        {
            // POST /admin/loyalty/programs/{programId}/tiers
            group.MapPost("programs/{programId:int}/tiers", async (
                int programId, [FromBody] CreateLoyaltyTierRequest req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(new CreateLoyaltyTierCommand(
                    programId, req.Code, req.Name, req.Rank, req.AllianceCode, req.AllianceTierCode), ct);
                return Results.Created($"/admin/loyalty/programs/{programId}/tiers/{id}", new { id });
            })
            .WithName("AdminCreateLoyaltyTier")
            .WithSummary("Add a tier to a loyalty program")
            .WithDescription("404 when the program does not exist. 409 when the program already has a tier with that "
                + "code or at that rank — loyalty_tiers is unique on both, per program.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);

            // PUT /admin/loyalty/programs/{programId}/tiers/{tierId}
            group.MapPut("programs/{programId:int}/tiers/{tierId:int}", async (
                int programId, int tierId, [FromBody] UpdateLoyaltyTierRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateLoyaltyTierCommand(
                    programId, tierId, req.Code, req.Name, req.Rank, req.AllianceCode, req.AllianceTierCode), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateLoyaltyTier")
            .WithSummary("Update one of a loyalty program's tiers")
            .WithDescription("404 when the program does not exist, or when the tier is not one of its tiers. "
                + "409 when another tier of the program already has that code or rank.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);

            // DELETE /admin/loyalty/programs/{programId}/tiers/{tierId}
            group.MapDelete("programs/{programId:int}/tiers/{tierId:int}", async (
                int programId, int tierId, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteLoyaltyTierCommand(programId, tierId), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteLoyaltyTier")
            .WithSummary("Delete one of a loyalty program's tiers")
            .WithDescription("404 when the program does not exist, or when the tier is not one of its tiers. "
                + "Travelers holding the tier keep their membership and lose the tier: a membership's TierId is "
                + "set to null rather than the delete failing.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404);
        }
    }
}
