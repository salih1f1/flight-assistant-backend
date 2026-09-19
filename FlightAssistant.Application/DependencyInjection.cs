using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Behaviours;
using FlightAssistant.Application.Commands.Admin.Verify;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Application.Verification;
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
            //
            // Two-registration form, not AddScoped<IAdminContext, AdminContext>(): that single
            // form would force every writer (the Program.cs endpoint filters) to
            // (AdminContext)GetRequiredService<IAdminContext>() to get at the settable
            // ActorId/ActorName — a cast that throws InvalidCastException at request time, in a
            // filter, on every admin request, the moment anything ever registers a different
            // IAdminContext implementation (e.g. a test double via
            // services.AddScoped<IAdminContext>(_ => new Fake...)). Registering AdminContext
            // itself, then IAdminContext as a resolver back to that same scoped instance, gives
            // both views of one object with no cast anywhere (#313 Task 2 review, F3/I3).
            services.AddScoped<AdminContext>();
            services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());

            // Reference verification job (#367). Scoped: the runner is resolved from the job's cycle
            // scope, and a processor from each request's own scope, so each saves on its own context.
            services.AddScoped<IVerificationCycleRunner, VerificationCycleRunner>();
            services.AddScoped<VerificationRequestProcessor>();

            // Auto-provisioning of a missing reference row on track (#465).
            services.AddScoped<IReferenceDataProvisioner, ReferenceDataProvisioner>();

            // The runtime provider-routing switch's read side for verification sources (#531),
            // falling back to Verification:DefaultSources:* -- consumed by both
            // FlightTrackedVerificationEventHandler and ReferenceDataProvisioner.
            services.AddScoped<IVerificationSourceDefaults, VerificationSourceDefaults>();

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
