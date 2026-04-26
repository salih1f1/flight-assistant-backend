using Serilog.Context;

namespace FlightAssistant.API.Middleware
{
    public class CorrelationIdMiddleware(RequestDelegate next)
    {
        private const string Header = "X-Correlation-Id";

        public async Task InvokeAsync(HttpContext ctx)
        {
            var correlationId = ctx.Request.Headers[Header].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N");

            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[Header] = correlationId;
                return Task.CompletedTask;
            });

            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                await next(ctx);
            }
        }
    }
}
