using FlightAssistant.Application.Commands.Admin.Verify;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminVerifyEndpoints
    {
        public static void MapAdminVerifyEndpoints(this IEndpointRouteBuilder admin)
        {
            MapVerifyEndpoint<Airport>(admin, "airports");
            MapVerifyEndpoint<Airline>(admin, "airlines");
            MapVerifyEndpoint<Alliance>(admin, "alliances");
            MapVerifyEndpoint<Country>(admin, "countries");
            MapVerifyEndpoint<Lounge>(admin, "lounges");
            MapVerifyEndpoint<LoungeProvider>(admin, "lounge-providers");
            MapVerifyEndpoint<LoyaltyProgram>(admin, "loyalty-programs");
            MapVerifyEndpoint<LoyaltyProgramKind>(admin, "loyalty-program-kinds");
        }

        private static void MapVerifyEndpoint<TEntity>(IEndpointRouteBuilder admin, string prefix)
            where TEntity : class, Domain.Common.Interfaces.IVerifiable
        {
            admin.MapPatch($"{prefix}/{{id:int}}/verify",
                async (int id, [FromBody] VerifyRequest req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                {
                    var verifiedBy = user.FindFirstValue("oid") ?? user.FindFirstValue("sub") ?? "admin";
                    var found = await mediator.Send(new VerifyEntityCommand<TEntity>(id, req.IsVerified, verifiedBy), ct);
                    return found ? Results.NoContent() : Results.NotFound();
                })
            .WithName($"AdminVerify{typeof(TEntity).Name}")
            .WithSummary($"Verify or unverify a {typeof(TEntity).Name}")
            .WithTags($"Admin - {prefix.Replace("-", " ").Replace("s ", " ").Trim()}")
            .Produces(204)
            .ProducesProblem(404);
        }
    }
}
