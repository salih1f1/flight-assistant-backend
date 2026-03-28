using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightStatus
{
    /// <summary>Calls AeroDataBox API to get live flight status.</summary>
    public sealed class AeroDataBoxFlightStatusProvider : IFlightStatusProvider
    {
        private readonly HttpClient _http;

        public string ProviderName => "AeroDataBox";

        public AeroDataBoxFlightStatusProvider(HttpClient http) => _http = http;

        public async Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, CancellationToken ct = default)
        {
            // AeroDataBox endpoint: GET /flights/Number/{flightNumber}/{date}
            var date = serviceDate.ToString("yyyy-MM-dd");
            var response = await _http.GetAsync($"flights/Number/{flightNumber}/{date}", ct);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // AeroDataBox returns an array of departures; take the first
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var flight = doc.RootElement[0];
            return MapToResult(flight, json);
        }

        private static FlightStatusResult? MapToResult(JsonElement flight, string rawJson)
        {
            // Status mapping from AeroDataBox status strings to our codes
            var statusStr = flight.TryGetProperty("status", out var s) ? s.GetString() : null;
            var statusCode = MapStatus(statusStr);

            var effectiveAt = TryGetDateTime(flight, "departure", "actualTimeUtc")
                ?? TryGetDateTime(flight, "departure", "revisedTimeUtc")
                ?? TryGetDateTime(flight, "departure", "scheduledTimeUtc")
                ?? DateTime.UtcNow;

            return new FlightStatusResult(
                StatusCode: statusCode,
                EffectiveAtUtc: effectiveAt,
                EstimatedDepartureUtc: TryGetDateTime(flight, "departure", "revisedTimeUtc"),
                EstimatedArrivalUtc: TryGetDateTime(flight, "arrival", "revisedTimeUtc"),
                ActualDepartureUtc: TryGetDateTime(flight, "departure", "actualTimeUtc"),
                ActualArrivalUtc: TryGetDateTime(flight, "arrival", "actualTimeUtc"),
                Gate: TryGetString(flight, "departure", "gate"),
                Terminal: TryGetString(flight, "departure", "terminal"),
                BaggageClaim: TryGetString(flight, "arrival", "baggageBelt"),
                EquipmentIcao: TryGetString(flight, "aircraft", "model"),
                ExternalId: flight.TryGetProperty("number", out var n) ? n.GetString() : null,
                RawPayloadJson: rawJson
            );
        }

        private static string MapStatus(string? status) => status?.ToLowerInvariant() switch
        {
            "scheduled" => "SCH",
            "departed" => "DEP",
            "landed" or "arrived" => "ARR",
            "cancelled" => "CAN",
            "delayed" => "DLY",
            "diverted" => "DIV",
            _ => "UNK"
        };

        private static DateTime? TryGetDateTime(JsonElement el, string section, string field)
        {
            if (el.TryGetProperty(section, out var sec) && sec.TryGetProperty(field, out var val))
                return DateTime.TryParse(val.GetString(), out var dt) ? dt.ToUniversalTime() : null;
            return null;
        }

        private static string? TryGetString(JsonElement el, string section, string field)
        {
            if (el.TryGetProperty(section, out var sec) && sec.TryGetProperty(field, out var val))
                return val.GetString();
            return null;
        }
    }
}
