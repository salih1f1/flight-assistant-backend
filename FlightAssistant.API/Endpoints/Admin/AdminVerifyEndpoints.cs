using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Admin.Verify;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
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
            MapVerifyByIdEndpoint<Airport>(admin, "airports");
            MapVerifyByIdEndpoint<Airline>(admin, "airlines");
            MapVerifyByIdEndpoint<Alliance>(admin, "alliances");
            MapVerifyByCodeEndpoint<Country>(admin, "countries");
            MapVerifyByIdEndpoint<Lounge>(admin, "lounges");
            MapVerifyByIdEndpoint<LoungeProvider>(admin, "lounge-providers");
            MapVerifyByIdEndpoint<LoyaltyProgram>(admin, "loyalty-programs");
            MapVerifyByIdEndpoint<LoyaltyProgramKind>(admin, "loyalty-program-kinds");
        }

        private static void MapVerifyByIdEndpoint<TEntity>(IEndpointRouteBuilder admin, string prefix)
            where TEntity : class, IVerifiable =>
            MapVerifyEndpoint<TEntity>(admin, prefix, $"{prefix}/{{id:int}}/verify",
                (int id, [FromBody] VerifyRequest req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                    VerifyAsync<TEntity>(id, req, user, mediator, ct));

        // Country is keyed by its 2-char Code, not an int id, so it gets its own route — the
        // {code} route parameter carries no constraint, so a malformed code still matches the
        // route and gets a real 404 rather than falling through to an unmatched-/admin-route
        // CORS failure. The code is canonicalised with the same ToCanonicalCode() Country.Create
        // uses, so "tr" finds "TR".
        private static void MapVerifyByCodeEndpoint<TEntity>(IEndpointRouteBuilder admin, string prefix)
            where TEntity : class, IVerifiable =>
            MapVerifyEndpoint<TEntity>(admin, prefix, $"{prefix}/{{code}}/verify",
                (string code, [FromBody] VerifyRequest req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                    VerifyAsync<TEntity>(code.ToCanonicalCode(), req, user, mediator, ct));

        private static void MapVerifyEndpoint<TEntity>(IEndpointRouteBuilder admin, string prefix, string routeTemplate, Delegate handler)
            where TEntity : class, IVerifiable
        {
            admin.MapPatch(routeTemplate, handler)
                .WithName($"AdminVerify{typeof(TEntity).Name}")
                .WithSummary($"Verify or unverify a {typeof(TEntity).Name}")
                .WithTags($"Admin - {prefix.Replace("-", " ").Replace("s ", " ").Trim()}")
                .Produces(204)
                .ProducesProblem(404);
        }

        private static async Task<IResult> VerifyAsync<TEntity>(object key, VerifyRequest req, ClaimsPrincipal user, IMediator mediator, CancellationToken ct)
            where TEntity : class, IVerifiable
        {
            var found = await mediator.Send(new VerifyEntityCommand<TEntity>(key, req.IsVerified, user.GetAdminActorId()), ct);
            return found ? Results.NoContent() : Results.NotFound();
        }
    }
}
