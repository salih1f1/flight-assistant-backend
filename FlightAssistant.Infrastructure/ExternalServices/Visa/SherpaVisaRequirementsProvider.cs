using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.Infrastructure.ExternalServices.Visa
{
    /// <summary>Calls the Sherpa° API to get visa / entry requirements.</summary>
    public sealed class SherpaVisaRequirementsProvider : IVisaRequirementsProvider
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly ILogger<SherpaVisaRequirementsProvider> _log;

        public SherpaVisaRequirementsProvider(HttpClient http, IConfiguration config,
            ILogger<SherpaVisaRequirementsProvider> log)
        {
            _http = http;
            _apiKey = config["Sherpa:ApiKey"] ?? string.Empty;
            _log = log;
        }

        public async Task<VisaRequirementResult?> GetRequirementsAsync(
            string passportCountryCode,
            string destinationCountryCode,
            CancellationToken ct = default)
        {
            // Sherpa° endpoint: POST /entry-requirements with passport + destination
            var payload = new
            {
                passport = new { country = passportCountryCode.ToUpperInvariant() },
                destination = new { country = destinationCountryCode.ToUpperInvariant() }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "entry-requirements")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Sherpa visa API unreachable for {Passport}→{Destination}",
                    passportCountryCode, destinationCountryCode);
                throw new DomainException(
                    "Visa requirements service is temporarily unavailable. Please try again later.", 503);
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Sherpa visa API returned {StatusCode} for {Passport}→{Destination}",
                    (int)response.StatusCode, passportCountryCode, destinationCountryCode);
                throw new DomainException(
                    "Visa requirements service is temporarily unavailable. Please try again later.", 503);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return MapToResult(passportCountryCode, destinationCountryCode, doc.RootElement, json);
        }

        private static VisaRequirementResult? MapToResult(
            string passport,
            string destination,
            JsonElement root,
            string rawJson)
        {
            // Sherpa° returns { "visa": { "required": bool, "type": string, "stay_days": int } }
            if (!root.TryGetProperty("visa", out var visa)) return null;

            var required = visa.TryGetProperty("required", out var req) && req.GetBoolean();
            var type = visa.TryGetProperty("type", out var t) ? t.GetString() : null;
            var stayDays = visa.TryGetProperty("stay_days", out var sd) && sd.ValueKind == JsonValueKind.Number
                ? sd.GetInt32() : (int?)null;

            var admissionType = required
                ? (type ?? "visa_required")
                : "visa_free";

            return new VisaRequirementResult(
                passport.ToUpperInvariant(),
                destination.ToUpperInvariant(),
                admissionType,
                stayDays,
                null,
                rawJson);
        }
    }
}
