using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightStatus
{
    /// <summary>Calls AviationStack API to get live flight status (used as fallback).</summary>
    public sealed class AviationStackFlightStatusProvider : IFlightStatusProvider
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        public string ProviderName => "AviationStack";

        public AviationStackFlightStatusProvider(HttpClient http, IConfiguration config)
        {
            _http = http;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        public async Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, CancellationToken ct = default)
        {
            var response = await _http.GetAsync(
                $"flights?access_key={_apiKey}&flight_iata={flightNumber}&flight_date={serviceDate:yyyy-MM-dd}", ct);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
                return null;

            return MapToResult(data[0], json);
        }

        private static FlightStatusResult MapToResult(JsonElement flight, string rawJson)
        {
            var statusStr = flight.TryGetProperty("flight_status", out var s) ? s.GetString() : null;
            var statusCode = MapStatus(statusStr);

            var dep = flight.TryGetProperty("departure", out var d) ? d : default;
            var arr = flight.TryGetProperty("arrival", out var a) ? a : default;

            var effectiveAt = TryGetDateTime(dep, "actual")
                ?? TryGetDateTime(dep, "estimated")
                ?? TryGetDateTime(dep, "scheduled")
                ?? DateTime.UtcNow;

            return new FlightStatusResult(
                StatusCode: statusCode,
                EffectiveAtUtc: effectiveAt,
                EstimatedDepartureUtc: TryGetDateTime(dep, "estimated"),
                EstimatedArrivalUtc: TryGetDateTime(arr, "estimated"),
                ActualDepartureUtc: TryGetDateTime(dep, "actual"),
                ActualArrivalUtc: TryGetDateTime(arr, "actual"),
                Gate: TryGetString(dep, "gate"),
                Terminal: TryGetString(dep, "terminal"),
                BaggageClaim: TryGetString(arr, "baggage"),
                EquipmentIcao: null, // AviationStack free tier doesn't provide aircraft ICAO
                ExternalId: flight.TryGetProperty("flight", out var fn)
                    && fn.TryGetProperty("iata", out var fiata) ? fiata.GetString() : null,
                RawPayloadJson: rawJson
            );
        }

        private static string MapStatus(string? status) => status?.ToLowerInvariant() switch
        {
            "scheduled" => "SCH",
            "active" => "DEP",
            "landed" => "ARR",
            "cancelled" => "CAN",
            "incident" => "DLY",
            "diverted" => "DIV",
            _ => "UNK"
        };

        private static DateTime? TryGetDateTime(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val) && val.ValueKind != JsonValueKind.Null)
                return DateTime.TryParse(
                    val.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt)
                        ? dt
                        : null;
            return null;
        }

        private static string? TryGetString(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val)) return val.GetString();
            return null;
        }
    }
}
