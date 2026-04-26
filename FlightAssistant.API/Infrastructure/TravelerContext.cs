using Serilog.Context;
using System.Security.Claims;

namespace FlightAssistant.API.Infrastructure
{
    public class TravelerContext
    {
        public Guid? TravelerId { get; set; }
    }

    public class TravelerContextMiddleware(RequestDelegate next)
    {
        public async Task InvokeAsync(HttpContext ctx, TravelerContext travelerContext)
        {
            var claim = ctx.User.FindFirstValue("oid") ?? ctx.User.FindFirstValue("sub");

            if (Guid.TryParse(claim, out var id))
            {
                travelerContext.TravelerId = id;
                using (LogContext.PushProperty("TravelerId", id))
                {
                    await next(ctx);
                }
            }
            else
            {
                await next(ctx);
            }
        }
    }
}
