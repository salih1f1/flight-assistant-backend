using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

class AirlineOut
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string Iata { get; set; } = default!;
    public string Icao { get; set; } = default!;
    public string Callsign { get; set; } = default!;
    public string CountryCode { get; set; } = default!;
    public bool Active { get; set; }
}

class Program
{
    const string OF_AIRLINES = "https://raw.githubusercontent.com/jpatokal/openflights/master/data/airlines.dat";
    const string OA_COUNTRIES = "https://ourairports.com/data/countries.csv";

    static async Task Main()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FlightAssistantSeeder", "airlines-iso2-1.0"));

        // 1) Load datasets
        var airlinesCsv = await http.GetStringAsync(OF_AIRLINES);
        var countriesCsv = await http.GetStringAsync(OA_COUNTRIES);

        // 2) Build country-name -> ISO2 map (from OurAirports countries.csv)
        var countryMap = BuildCountryMap(countriesCsv);        // normalizedName -> ISO2
        var overrides = BuildCountryOverrides();              // common edge cases

        // 3) Parse OpenFlights airlines and produce output
        var list = new List<AirlineOut>(2500);
        using (var sr = new StringReader(airlinesCsv))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                // airports.dat columns:
                // 0:AirlineID,1:Name,2:Alias,3:IATA,4:ICAO,5:Callsign,6:Country,7:Active
                var f = SplitCsv(line);
                if (f.Length < 8) continue;

                string name = T(f[1]);
                string iata = T(f[3]);
                string icao = T(f[4]);
                string callsign = T(f[5]);
                string country = T(f[6]);
                string active = T(f[7]);

                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(iata) || 
                    string.IsNullOrWhiteSpace(icao) || 
                    string.IsNullOrWhiteSpace(callsign) || 
                    string.IsNullOrWhiteSpace(country))
                {
                    continue;
                }

                if (iata.ToLower() == "n/a" || iata.ToLower() == "\\n" || 
                    icao.ToLower() == "n/a" || icao.ToLower() == "\\n")
                {
                    continue;
                }

                if (list.Any(x => x.Iata == iata))
                {
                    continue;
                }

                if (list.Any(x => x.Icao == icao))
                {
                    continue;
                }

                // Keep only active airlines that have at least IATA or ICAO
                bool isActive = active.Equals("Y", StringComparison.OrdinalIgnoreCase);
                if (!isActive) continue;

                string? iso2 = LookupIso2(country, countryMap, overrides);

                if (string.IsNullOrEmpty(iso2)) continue;

                list.Add(new AirlineOut
                {
                    Id = 0,
                    Name = name,
                    Iata = iata,
                    Icao = icao,
                    Callsign = callsign,
                    CountryCode = iso2,   // may be null for very rare edge cases
                    Active = true
                });
            }
        }

        // Stable order & Ids
        list = list
            .OrderBy(a => a.CountryCode ?? "ZZ", StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Iata, StringComparer.Ordinal)
            .ThenBy(a => a.Icao, StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < list.Count; i++) list[i].Id = i + 1;

        // 4) Write JSON
        var optsPretty = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var optsCompact = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        await File.WriteAllTextAsync("airlines.pretty.json", JsonSerializer.Serialize(list, optsPretty));
        await File.WriteAllTextAsync("airlines.json", JsonSerializer.Serialize(list, optsCompact));

        Console.WriteLine($"✅ Wrote {list.Count:N0} airlines. Null CountryCode: {list.Count(a => a.CountryCode == null):N0}");
    }

    // -------- Country mapping helpers --------

    static Dictionary<string, string> BuildCountryMap(string countriesCsv)
    {
        // OurAirports countries.csv header: code,name,continent,wikipedia_link,keywords
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var sr = new StringReader(countriesCsv);
        var header = sr.ReadLine();
        if (header == null) return map;
        var cols = SplitCsv(header);
        int ci_code = Array.FindIndex(cols, h => Eq(h, "code"));
        int ci_name = Array.FindIndex(cols, h => Eq(h, "name"));
        int ci_kw = Array.FindIndex(cols, h => Eq(h, "keywords"));

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var f = SplitCsv(line);
            string code = T(Get(f, ci_code));
            string name = T(Get(f, ci_name));
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var key = NormalizeName(name);
            map[key] = code;

            // Also index keywords as alternative names (comma-separated)
            if (ci_kw >= 0 && ci_kw < f.Length)
            {
                var kw = T(f[ci_kw]);
                if (!string.IsNullOrWhiteSpace(kw))
                {
                    foreach (var alt in kw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var k2 = NormalizeName(alt);
                        if (!map.ContainsKey(k2)) map[k2] = code;
                    }
                }
            }
        }
        return map;

        static bool Eq(string a, string b) => a.Trim('"').Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    static Dictionary<string, string> BuildCountryOverrides()
    {
        // OpenFlights sometimes uses shorter or legacy names—map them explicitly.
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["UK"] = "GB",
            ["United Kingdom"] = "GB",
            ["Great Britain"] = "GB",
            ["UAE"] = "AE",
            ["United States"] = "US",
            ["Russia"] = "RU",
            ["Czech Republic"] = "CZ",
            ["South Korea"] = "KR",
            ["North Korea"] = "KP",
            ["Iran"] = "IR",
            ["Syria"] = "SY",
            ["Vietnam"] = "VN",
            ["Laos"] = "LA",
            ["Brunei"] = "BN",
            ["Ivory Coast"] = "CI",
            ["Cape Verde"] = "CV",
            ["Burma"] = "MM",
            ["Congo (Kinshasa)"] = "CD",
            ["Congo (Brazzaville)"] = "CG",
            ["Palestinian Territory"] = "PS",
            ["Kosovo"] = "XK",              // widely used pseudo-ISO
            ["Macedonia"] = "MK",           // legacy
            ["North Macedonia"] = "MK",
            ["Moldova"] = "MD",
            ["Tanzania"] = "TZ",
            ["Venezuela"] = "VE",
            ["Bolivia"] = "BO",
            ["Micronesia"] = "FM",
            ["Reunion"] = "RE",
            ["Curacao"] = "CW",
            ["Sint Maarten"] = "SX",
            ["Saint Martin"] = "MF",
            ["Saint Barthelemy"] = "BL",
            ["Hong Kong"] = "HK",
            ["Macau"] = "MO",
            ["Taiwan"] = "TW",
            ["Eswatini"] = "SZ",
            ["Swaziland"] = "SZ",
            ["East Timor"] = "TL"
        };
    }

    static string? LookupIso2(string countryName, Dictionary<string, string> map, Dictionary<string, string> overrides)
    {
        if (string.IsNullOrWhiteSpace(countryName)) return null;

        // direct override first
        if (overrides.TryGetValue(countryName, out var o)) return o;

        // normalized match to map
        var norm = NormalizeName(countryName);
        if (map.TryGetValue(norm, out var code)) return code;

        // last-chance: try stripping parentheticals entirely
        var noParen = StripParentheses(countryName).Trim();
        if (!countryName.Equals(noParen, StringComparison.Ordinal))
        {
            var norm2 = NormalizeName(noParen);
            if (map.TryGetValue(norm2, out code)) return code;
            if (overrides.TryGetValue(noParen, out o)) return o;
        }

        return null;
    }

    // -------- Utilities --------

    static string[] SplitCsv(string line)
    {
        var list = new List<string>();
        bool inQ = false;
        var cur = new StringBuilder();
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

    static string Get(string[] f, int i) => (i >= 0 && i < f.Length) ? f[i] : "";
    static string T(string s) => s.Trim().Trim('"');

    static string NormalizeName(string s)
    {
        s = StripParentheses(s);
        s = s.Replace('&', ' ');
        s = s.Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'");
        s = RemoveDiacritics(s);
        s = s.ToUpperInvariant();
        s = new string(s.Where(ch => char.IsLetter(ch) || char.IsWhiteSpace(ch)).ToArray());
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s;
    }

    static string StripParentheses(string s)
    {
        var sb = new StringBuilder();
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')') { if (depth > 0) depth--; }
            else if (depth == 0) sb.Append(ch);
        }
        return sb.ToString();
    }

    static string RemoveDiacritics(string text)
    {
        var norm = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
        {
            var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}