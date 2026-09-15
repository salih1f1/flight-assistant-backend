using FlightAssistant.Application.Administration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// The actor the verification job works as (#367, spec §4.2). <c>AdminAuditInterceptor</c> writes
    /// no audit row for a save without an actor, so every DI scope in which the job saves must carry
    /// this one; the job's <c>Verify</c> and <c>MarkVerificationFailed</c> writes are then audited
    /// like an admin's. The same id is the <c>verifiedBy</c> of the job's <c>Verify</c> calls.
    /// </summary>
    public static class VerificationJobActor
    {
        public const string ActorId = "system";
        public const string ActorName = "Verification job";

        /// <summary>Sets the system actor, and <paramref name="correlationId"/>, on a scope's
        /// <see cref="AdminContext"/>.</summary>
        public static void ApplyTo(AdminContext adminContext, string? correlationId = null)
        {
            ArgumentNullException.ThrowIfNull(adminContext);
            adminContext.ActorId = ActorId;
            adminContext.ActorName = ActorName;
            adminContext.CorrelationId = correlationId;
        }

        /// <summary>A new DI scope whose <see cref="AdminContext"/> already carries the system actor.
        /// The caller disposes it.</summary>
        public static IServiceScope CreateScope(IServiceScopeFactory scopeFactory, string? correlationId = null)
        {
            ArgumentNullException.ThrowIfNull(scopeFactory);

            var scope = scopeFactory.CreateScope();
            try
            {
                ApplyTo(scope.ServiceProvider.GetRequiredService<AdminContext>(), correlationId);
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }
    }
}
