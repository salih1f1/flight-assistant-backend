using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

class AirportOut
{
    public int Id { get; set; }
    public string Iata { get; set; } = default!;
    public string? Icao { get; set; }
    public string Name { get; set; } = default!;
    public string City { get; set; } = default!;
    public string CountryCode { get; set; } = default!;

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? ElevationFt { get; set; }
    public string? Timezone { get; set; }
}

class Program
{
    static async Task Main()
    {
        // Sources
        const string OA_AIRPORTS = "https://ourairports.com/airports.csv";
        const string OF_AIRPORTS = "https://raw.githubusercontent.com/jpatokal/openflights/master/data/airports.dat";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FlightAssistantSeeder", "1.1"));

        // Download datasets
        var oaCsv = await http.GetStringAsync(OA_AIRPORTS);
        var ofCsv = await http.GetStringAsync(OF_AIRPORTS);

        // -------- Parse OpenFlights for TZ (IANA) --------
        // airports.dat columns (CSV with quotes):
        // 0:AirportID,1:Name,2:City,3:Country,4:IATA,5:ICAO,6:Lat,7:Lon,8:Alt,9:TzOffset,10:DST,11:TzDB,12:Type,13:Source
        var tzByIata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tzByIcao = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (var sr = new StringReader(ofCsv))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var f = SplitCsv(line);
                if (f.Length < 12) continue;

                string iata = TrimQuotes(f[4]);
                string icao = TrimQuotes(f[5]);
                string tz = f.Length > 11 ? TrimQuotes(f[11]) : "";

                if (!string.IsNullOrWhiteSpace(tz))
                {
                    if (!string.IsNullOrWhiteSpace(iata) && iata.Length == 3)
                        tzByIata[iata] = tz;
                    if (!string.IsNullOrWhiteSpace(icao) && icao.Length >= 3)
                        tzByIcao[icao] = tz;
                }
            }
        }

        // -------- Parse OurAirports, keep only IATA, not closed, dedupe by "most airport-like" type --------
        using var srOA = new StringReader(oaCsv);
        var header = srOA.ReadLine();
        if (header is null) throw new Exception("Empty OurAirports CSV.");

        var cols = SplitCsv(header);
        int ci_type = Col("type");
        int ci_name = Col("name");
        int ci_lat = Col("latitude_deg");
        int ci_lon = Col("longitude_deg");
        int ci_elev = Col("elevation_ft");
        int ci_ctry = Col("iso_country");
        int ci_muni = Col("municipality");
        int ci_gps = Col("gps_code");
        int ci_iata = Col("iata_code");

        int Col(string name) => Array.FindIndex(cols, h => string.Equals(TrimQuotes(h), name, StringComparison.OrdinalIgnoreCase));
        string? lineOA;

        static int TypeRank(string t) => t switch
        {
            "large_airport" => 400,
            "medium_airport" => 300,
            "small_airport" => 200,
            "seaplane_base" => 120,
            "heliport" => 110,
            "balloonport" => 100,
            "closed" => -1,
            _ => 50
        };

        var bestByIata = new Dictionary<string, (int rank, AirportOut ap)>(StringComparer.OrdinalIgnoreCase);

        while ((lineOA = srOA.ReadLine()) != null)
        {
            var f = SplitCsv(lineOA);
            var iata = TrimQuotes(Val(f, ci_iata));
            if (string.IsNullOrWhiteSpace(iata)) continue;

            var type = TrimQuotes(Val(f, ci_type));
            if (string.Equals(type, "closed", StringComparison.OrdinalIgnoreCase)) continue;

            var icao = TrimToNull(Val(f, ci_gps));
            var ap = new AirportOut
            {
                Iata = iata,
                Icao = icao,
                Name = TrimQuotes(Val(f, ci_name)),
                City = TrimToNull(Val(f, ci_muni)) ?? "",
                CountryCode = TrimQuotes(Val(f, ci_ctry)),
                Latitude = TryDouble(Val(f, ci_lat)),
                Longitude = TryDouble(Val(f, ci_lon)),
                ElevationFt = TryInt(Val(f, ci_elev)),
                Timezone = null // fill below
            };

            var rank = TypeRank(type);
            if (!bestByIata.TryGetValue(iata, out var cur) || rank > cur.rank)
                bestByIata[iata] = (rank, ap);
        }

        // -------- Enrich Timezones from OpenFlights (IATA -> TZ, else ICAO -> TZ) --------
        int tzHitIata = 0, tzHitIcao = 0;
        foreach (var kv in bestByIata.Keys.ToList())
        {
            var ap = bestByIata[kv].ap;
            if (tzByIata.TryGetValue(ap.Iata, out var tz))
            {
                ap.Timezone = tz;
                tzHitIata++;
            }
            else if (!string.IsNullOrWhiteSpace(ap.Icao) && tzByIcao.TryGetValue(ap.Icao!, out tz))
            {
                ap.Timezone = tz;
                tzHitIcao++;
            }
        }

        // -------- Produce stable order and Ids (by IATA for determinism) --------
        var list = bestByIata.Values.Select(x => x.ap)
            .OrderBy(a => a.Iata, StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < list.Count; i++) list[i].Id = i + 1;

        var opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        await File.WriteAllTextAsync("airports.json", JsonSerializer.Serialize(list, opts));
        opts.WriteIndented = true;
        await File.WriteAllTextAsync("airports.pretty.json", JsonSerializer.Serialize(list, opts));

        Console.WriteLine($"✅ Airports: {list.Count:N0} | TZ by IATA: {tzHitIata:N0} | TZ by ICAO: {tzHitIcao:N0} | Missing TZ: {list.Count - tzHitIata - tzHitIcao:N0}");

        // ---- helpers ----
        static string Val(string[] f, int i) => i >= 0 && i < f.Length ? f[i] : "";
        static string TrimQuotes(string s) => s.Trim().Trim('"');
        static string? TrimToNull(string s)
        {
            var t = TrimQuotes(s);
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        static double? TryDouble(string s) =>
            double.TryParse(TrimQuotes(s), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        static int? TryInt(string s) =>
            int.TryParse(TrimQuotes(s), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;

        // CSV splitter that handles quotes + escaped quotes ("")
        static string[] SplitCsv(string line)
        {
            var list = new List<string>();
            bool inQ = false;
            var cur = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQ && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQ = !inQ;
                }
                else if (c == ',' && !inQ)
                {
                    list.Add(cur.ToString());
                    cur.Clear();
                }
                else cur.Append(c);
            }
            list.Add(cur.ToString());
            return list.ToArray();
        }
    }
}