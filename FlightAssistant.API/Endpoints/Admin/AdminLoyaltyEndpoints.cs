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
                    p.Id, p.Code, p.Name, p.Kind, p.AirlineId, p.AllianceCode,
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
                var id = await mediator.Send(new CreateLoyaltyProgramCommand(req.Code, req.Name, req.KindId, req.AirlineId, req.AllianceCode), ct);
                return Results.Created($"/admin/loyalty/programs/{id}", new { id });
            })
            .WithName("AdminCreateLoyaltyProgram")
            .WithSummary("Create loyalty program")
            .Produces(201)
            .ProducesValidationProblem();

            // PUT /admin/loyalty/programs/{id}
            group.MapPut("programs/{id:int}", async (int id, [FromBody] UpdateLoyaltyProgramRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateLoyaltyProgramCommand(id, req.Code, req.Name, req.KindId, req.AirlineId, req.AllianceCode), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateLoyaltyProgram")
            .WithSummary("Update loyalty program")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404);

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
        }
    }
}
