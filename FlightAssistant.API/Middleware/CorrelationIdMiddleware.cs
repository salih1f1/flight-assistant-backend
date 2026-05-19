using Serilog.Context;

namespace FlightAssistant.API.Middleware
{
    public class CorrelationIdMiddleware(RequestDelegate next)
    {
        private const string CorrelationIdHeader = "X-Correlation-Id";

        public async Task InvokeAsync(HttpContext ctx)
        {
            var rawId = ctx.Request.Headers[CorrelationIdHeader].FirstOrDefault();
            var correlationId = IsValidCorrelationId(rawId)
                ? rawId!
                : Guid.NewGuid().ToString("N");

            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[CorrelationIdHeader] = correlationId;
                return Task.CompletedTask;
            });

            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                await next(ctx);
            }
        }

        private static bool IsValidCorrelationId(string? value)
            => value is { Length: > 0 and <= 64 } &&
               value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}
