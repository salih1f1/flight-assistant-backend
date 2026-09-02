using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Behaviours;
using FlightAssistant.Application.Commands.Admin.Verify;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace FlightAssistant.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddMediatR(cfg =>
                cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));

            // Scoped so the Task 2 admin endpoint filter/middleware can populate it once per
            // request and the Infrastructure audit interceptor reads back the same instance
            // within that request's DbContext scope.
            services.AddScoped<IAdminContext, AdminContext>();

            // VerifyEntityCommandHandler is a generic handler; register a concrete instance per entity type
            // so MediatR's assembly scan (which doesn't resolve open generics) can find them.
            services.AddTransient<IRequestHandler<VerifyEntityCommand<Airport>, bool>, VerifyEntityCommandHandler<Airport>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<Airline>, bool>, VerifyEntityCommandHandler<Airline>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<Alliance>, bool>, VerifyEntityCommandHandler<Alliance>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<Country>, bool>, VerifyEntityCommandHandler<Country>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<Lounge>, bool>, VerifyEntityCommandHandler<Lounge>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<LoungeProvider>, bool>, VerifyEntityCommandHandler<LoungeProvider>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<LoyaltyProgram>, bool>, VerifyEntityCommandHandler<LoyaltyProgram>>();
            services.AddTransient<IRequestHandler<VerifyEntityCommand<LoyaltyProgramKind>, bool>, VerifyEntityCommandHandler<LoyaltyProgramKind>>();

            return services;
        }
    }
}
