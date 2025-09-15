// TO START: node build-countries.js > countries.json

// build-countries.js
// Creates a countries.json array with fields:
// Code (alpha-2), Name, Alpha3, NumericCode, Region (continent), Subregion,
// IsEU, IsSchengen, IsEEA

// Data sources:
// - Countries baseline: https://restcountries.com/v3.1/all
// - EU membership list (current states), Schengen, EEA: curated sets below
//
// Notes:
// - "Region" aligns with REST Countries' continent ("Africa", "Americas", "Asia", "Europe", "Oceania", "Antarctic").
// - "Subregion" is REST Countries' subregion (e.g., "Western Europe").
// - NumericCode is zero-padded to 3 digits where available.

const EU_ALPHA2 = new Set([
    "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT",
    "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"
]);

// Schengen members (incl. non-EU: NO, IS, CH, LI)
// As of 2025, note: RO and BG joined Schengen for air/sea in 2024–2025 and land in 2025.
// If you need “full land/air/sea” only, adjust flags accordingly.
const SCHENGEN_ALPHA2 = new Set([
    "AT", "BE", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IS", "IT", "LV", "LI", "LT",
    "LU", "MT", "NL", "NO", "PL", "PT", "SK", "SI", "ES", "SE", "CH", "HR", "RO", "BG"
]);

// EEA (EU + Iceland, Liechtenstein, Norway). Switzerland is NOT EEA.
const EEA_ALPHA2 = new Set([
    ...EU_ALPHA2,
    "IS", "LI", "NO"
]);

// Countries with widely recognized ISO 3166-1 codes but not UN members are still included by REST Countries.
// You can post-filter if you only want UN members/observers.
const fetchJson = (url) => fetch(url).then(r => {
    if (!r.ok) throw new Error(`HTTP ${r.status} for ${url}`);
    return r.json();
});

(async () => {
    const all = await fetchJson("https://restcountries.com/v3.1/all?fields=cca2,cca3,ccn3,name,region,subregion");
    // Normalize and sort by Name for stable output
    const mapped = all
        .filter(c => c.cca2 && c.cca3 && c.name && c.name.common)
        .map(c => {
            const code = c.cca2.toUpperCase();
            const alpha3 = c.cca3.toUpperCase();
            const numericCode = (c.ccn3 || "").padStart(3, "0");
            const name = c.name.common;
            const region = c.region || "";        // e.g. "Europe"
            const subregion = c.subregion || "";  // e.g. "Western Europe"

            return {
                code,
                name,
                alpha3,
                numericCode,
                region,
                subregion,
                IsEU: EU_ALPHA2.has(code),
                IsSchengen: SCHENGEN_ALPHA2.has(code),
                IsEEA: EEA_ALPHA2.has(code)
            };
        })
        // Some REST Countries entries are territories/areas; keep or filter as you prefer:
        // .filter(x => x.Region) // uncomment to drop entities without a standard region
        .sort((a, b) => a.name.localeCompare(b.name, "en"));

    // Optional sanity checks (duplicates, missing codes)
    const seen2 = new Set();
    for (const r of mapped) {
        if (seen2.has(r.code)) {
            console.warn(`Duplicate alpha2: ${r.code} (${r.name})`);
        }
        seen2.add(r.code);
    }

    console.log(JSON.stringify(mapped, null, 2));
})();
