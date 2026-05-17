using System.Text.Json;

namespace FlightAssistant.API.Csv;

internal static class CsvJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
