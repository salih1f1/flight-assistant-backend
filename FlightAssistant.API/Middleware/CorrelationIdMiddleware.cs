using Serilog.Context;

namespace FlightAssistant.API.Middleware
{
    /// <summary>
    /// Mints or accepts the one correlation id this codebase treats as canonical (#319). Before
    /// this, three different identifiers competed for the same job — this one (always present:
    /// response header + every Serilog line), <c>Activity.Current?.Id</c> (only present with App
    /// Insights configured), and <c>HttpContext.TraceIdentifier</c> (always present, but in no log
    /// line) — and the two places that most needed correlation, <c>admin_audit_log.TraceId</c> and
    /// ProblemDetails' <c>traceId</c>, both used the one least likely to exist.
    ///
    /// <para>
    /// Exposed to the rest of the request via <see cref="HttpContext.Items"/>
    /// (<see cref="ItemsKey"/>), not a scoped DI service: <see cref="GlobalExceptionHandler"/> is
    /// registered as a singleton (ASP.NET Core's own <c>AddExceptionHandler&lt;T&gt;</c>
    /// convention), so it cannot take a scoped dependency by constructor injection without a
    /// captive-dependency problem — it would have to resolve one from <c>ctx.RequestServices</c> by
    /// hand regardless, which is no simpler than the dictionary already sitting on the
    /// <see cref="HttpContext"/> it's handed. Infrastructure and Application, which cannot see
    /// <see cref="HttpContext"/> at all, read the same value one hop later off
    /// <c>IAdminContext.CorrelationId</c> — populated by the <c>/admin</c> endpoint filter
    /// (<c>Program.cs</c>), which does have the <see cref="HttpContext"/>.
    /// </para>
    /// </summary>
    public class CorrelationIdMiddleware(RequestDelegate next)
    {
        private const string CorrelationIdHeader = "X-Correlation-Id";

        /// <summary>
        /// <see cref="HttpContext.Items"/> key the correlation id is stashed under for the
        /// lifetime of the request. One constant so every reader (<see cref="GlobalExceptionHandler"/>,
        /// the <c>/admin</c> endpoint filter) and the Serilog property below agree on the name.
        /// </summary>
        public const string ItemsKey = "CorrelationId";

        public async Task InvokeAsync(HttpContext ctx)
        {
            var rawId = ctx.Request.Headers[CorrelationIdHeader].FirstOrDefault();
            var correlationId = IsValidCorrelationId(rawId)
                ? rawId!
                : Guid.NewGuid().ToString("N");

            ctx.Items[ItemsKey] = correlationId;

            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[CorrelationIdHeader] = correlationId;
                return Task.CompletedTask;
            });

            using (LogContext.PushProperty(ItemsKey, correlationId))
            {
                await next(ctx);
            }
        }

        private static bool IsValidCorrelationId(string? value)
            => value is { Length: > 0 and <= 64 } &&
               value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}
