using System.Diagnostics;
using CsvHelper;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Middleware
{
    public class GlobalExceptionHandler : IExceptionHandler
    {
        private readonly ILogger<GlobalExceptionHandler> _log;
        private readonly IHostEnvironment _env;

        public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log, IHostEnvironment env)
        {
            _log = log;
            _env = env;
        }

        public async ValueTask<bool> TryHandleAsync(
            HttpContext ctx, Exception ex, CancellationToken ct)
        {
            switch (ex)
            {
                case FluentValidation.ValidationException ve:
                    var errors = ve.Errors
                        .GroupBy(f => f.PropertyName)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(f => f.ErrorMessage).ToArray());

                    var validationProblem = new ValidationProblemDetails(errors)
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Title = "Validation failed",
                        Instance = ctx.Request.Path
                    };

                    await WriteAsync(ctx, validationProblem, StatusCodes.Status400BadRequest, ct);
                    return true;

                case DomainException de:
                    var domainProblem = new ProblemDetails
                    {
                        Status = de.StatusCode,
                        Title = de.Message,
                        Instance = ctx.Request.Path,
                        // The underlying cause is for operators, not end users: outside Production
                        // it is surfaced here to make failures diagnosable from the response alone,
                        // while Production callers get only the title and the trace id.
                        Detail = _env.IsProduction() ? null : de.DiagnosticDetail
                    };

                    // For upstream throttling (503), hint clients when to retry.
                    if (de.StatusCode == StatusCodes.Status503ServiceUnavailable)
                        ctx.Response.Headers.RetryAfter = "60";

                    await WriteAsync(ctx, domainProblem, de.StatusCode, ct);
                    return true;

                case CsvHelperException csvEx:
                    var csvProblem = new ProblemDetails
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Title = "Invalid CSV file",
                        Detail = csvEx.Message,
                        Instance = ctx.Request.Path,
                        Type = "https://tools.ietf.org/html/rfc7807"
                    };

                    await WriteAsync(ctx, csvProblem, StatusCodes.Status400BadRequest, ct);
                    return true;

                default:
                    _log.LogError(ex, "Unhandled exception on {Method} {Path}",
                        ctx.Request.Method, ctx.Request.Path);

                    var serverProblem = new ProblemDetails
                    {
                        Status = StatusCodes.Status500InternalServerError,
                        Title = "An unexpected error occurred.",
                        Instance = ctx.Request.Path
                    };

                    await WriteAsync(ctx, serverProblem, StatusCodes.Status500InternalServerError, ct);
                    return true;
            }
        }

        /// <summary>
        /// Stamps every problem response with the request's correlation id (#319) — the same
        /// string <see cref="CorrelationIdMiddleware"/> already put on the <c>X-Correlation-Id</c>
        /// response header and every Serilog line for this request — so a caller can quote it and
        /// an operator can find the matching log entry from the response alone. Falls back to
        /// <see cref="Activity.Current"/>'s id, then <see cref="HttpContext.TraceIdentifier"/>, only
        /// for the case <see cref="CorrelationIdMiddleware"/> never ran — it is unconditional on
        /// every real request, so this is defensive, not the expected path — then writes the
        /// problem at the given status code.
        /// </summary>
        private static Task WriteAsync(HttpContext ctx, ProblemDetails problem, int statusCode, CancellationToken ct)
        {
            problem.Extensions["traceId"] = ctx.Items[CorrelationIdMiddleware.ItemsKey] as string
                ?? Activity.Current?.Id
                ?? ctx.TraceIdentifier;
            ctx.Response.StatusCode = statusCode;

            // RFC 9457: a problem document is application/problem+json, which is what every
            // .Produces*/.ProducesProblem declaration already publishes for these responses (#415).
            // Passing null here sent application/json instead, so handler-produced problems
            // contradicted the spec while the framework's own (model binding, Results.NotFound())
            // matched it. Not routed through IProblemDetailsService deliberately: that would let
            // ASP.NET fill Type and overwrite Title, changing bodies this fix must leave alone.
            return ctx.Response.WriteAsJsonAsync(
                problem, problem.GetType(), options: null, contentType: "application/problem+json", ct);
        }
    }
}
