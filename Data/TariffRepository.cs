using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using TariffHub.Models;

namespace TariffHub.Data;

/// <summary>Thrown when the live table lacks a column this file's SQL depends on.</summary>
public sealed class SchemaMismatchException(string table, IEnumerable<string> missing)
    : Exception($"{table} is missing column(s) the application expects: {string.Join(", ", missing)}. " +
                "Column names in Data/TariffRepository.cs must be aligned with the live schema.");

/// <param name="AnyValue">A stored value meaning "applies to every value" (e.g. piece_type ANY): matches any choice, never offered.</param>
/// <param name="NullMatchesAll">NULL means the tariff makes no distinction, so the row matches any choice.</param>
/// <summary>One airport of tariff.airport: the code, its full name, city and country.</summary>
public sealed record AirportInfo(string Code, string Name, string? City, string Country);

public sealed record FilterDef(string Key, string Label, string[] ColumnCandidates, string? AnyValue = null, bool NullMatchesAll = false);

/// <summary>
/// One end of the user's trip, already resolved against the master data by SearchService.
/// Every array may be empty; none is ever null.
/// </summary>
/// <param name="Country">ISO country code, or null for "any country".</param>
/// <param name="CityValue">Exact tariff city (CITY lanes).</param>
/// <param name="LocationPattern">Regex for tariff city values that stand for a named warehouse.</param>
/// <param name="StateSpellings">Upper-case spellings of the chosen state / province / prefecture (PROVINCE lanes).</param>
/// <param name="DistrictCarriers">With <paramref name="DistrictZones"/>: carrier districts containing the chosen subdivision (REGION lanes).</param>
/// <param name="RegionValue">Exact tariff region (REGION lanes of carriers without a district map).</param>
/// <param name="Postcode">User postcode; POSTCODE lanes match by prefix.</param>
/// <param name="AirportValue">IATA code (AIRPORT lanes).</param>
public sealed record PlaceFilter(
    string? Country, string? CityValue, string? LocationPattern, string[] StateSpellings,
    string[] DistrictCarriers, string[] DistrictZones, string? RegionValue, string? Postcode, string? AirportValue = null)
{
    public static readonly PlaceFilter Any = new(null, null, null, [], [], [], null, null);

    /// <summary>Something narrower than the country was given.</summary>
    public bool HasPlace => CityValue is not null || LocationPattern is not null || StateSpellings.Length > 0 || AirportValue is not null
                            || DistrictCarriers.Length > 0 || RegionValue is not null || Postcode is not null;
}

/// <summary>
/// Every SQL string in the application lives in this file. The database is read-only from here:
/// no DDL, no DML. Column names are whitelisted constants; user input only ever travels as parameters.
/// </summary>
public sealed class TariffRepository
{
    private readonly string? _connectionString;
    private readonly ConcurrentDictionary<TariffMode, HashSet<string>> _columns = new();

    public TariffRepository(string? connectionString)
    {
        _connectionString = connectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    // ---------------------------------------------------------------- schema

    public static string TableName(TariffMode mode) =>
        mode == TariffMode.Ground ? "tariff.ground_tariff" : "tariff.air_tariff";

    /// <summary>Columns the SQL below cannot work without, in both tables.</summary>
    private static readonly string[] RequiredColumns =
    [
        "record_type", "carrier_code", "service_type", "lane_type", "lane_code",
        "origin_point_type", "dest_point_type", "origin_country_code", "dest_country_code",
        "origin_region", "dest_region",
        "weight_from_kg", "weight_to_kg", "rate_value", "charge_basis", "min_charge", "currency_code", "status",
        "valid_from", "valid_to", "volumetric_kg_per_cbm", "min_chargeable_weight_kg",
        "auto_apply", "applies_to_cargo", "cargo_category", "max_charge", "charge_code",
        "source_ref", "source_sheet", "source_row",
    ];

    /// <summary>
    /// Optional filters, with the semantics the column comments give them. A filter whose column is absent
    /// from a table (air has no equipment, temperature, OOG or delivery point) is not offered for that mode.
    /// </summary>
    public static readonly IReadOnlyList<FilterDef> Filters = new FilterDef[]
    {
        new("service", "Service", ["service_type"]),
        new("level", "Service level", ["service_level"]),
        new("lanetype", "Lane type", ["lane_type"]),
        new("ratetag", "Rate tag", ["rate_tag"]),
        new("piece", "Piece type", ["piece_type"], AnyValue: "ANY"),
        new("delivery", "Delivery address", ["delivery_point"], NullMatchesAll: true),
        new("cargo", "Cargo type", ["cargo_category"], AnyValue: "ANY"),
        new("temperature", "Temperature", ["temperature_range"]),
        new("equipment", "Truck / container", ["equipment_type"]),
        new("oversize", "Oversize class", ["oog_class"]),
    };

    /// <summary>Point type → column suffix holding the place for that type ({origin|dest}_{suffix}).</summary>
    public static readonly IReadOnlyDictionary<string, string> PlaceColumnSuffix = new Dictionary<string, string>
    {
        ["CITY"] = "city",
        ["POSTCODE"] = "postcode",
        ["REGION"] = "region",
        ["PROVINCE"] = "state",
        ["AIRPORT"] = "airport",
    };

    private const string ColumnsSql = """
        SELECT column_name
        FROM information_schema.columns
        WHERE table_schema = 'tariff' AND table_name = @Table
        """;

    public HashSet<string> Columns(TariffMode mode)
    {
        if (_columns.TryGetValue(mode, out var cached)) return cached;

        using var conn = Open();
        var table = mode == TariffMode.Ground ? "ground_tariff" : "air_tariff";
        var cols = conn.Query<string>(ColumnsSql, new { Table = table }).ToHashSet();
        var missing = RequiredColumns.Where(c => !cols.Contains(c)).ToList();
        if (missing.Count > 0) throw new SchemaMismatchException(TableName(mode), missing);

        _columns[mode] = cols;
        return cols;
    }

    public static string? FilterColumn(FilterDef def, HashSet<string> cols) =>
        def.ColumnCandidates.FirstOrDefault(cols.Contains);

    public static bool HasDistance(HashSet<string> cols) =>
        cols.Contains("distance_from_km") && cols.Contains("distance_to_km");

    /// <summary>Place types the table can answer: the column must exist on both sides.</summary>
    public static IEnumerable<string> PlaceTypesSupported(HashSet<string> cols) =>
        PlaceColumnSuffix.Where(kv => cols.Contains($"origin_{kv.Value}") && cols.Contains($"dest_{kv.Value}"))
                         .Select(kv => kv.Key);

    // ---------------------------------------------------------- anonymisation

    /// <summary>Client letter + digit, then the carrier part, e.g. A1C01.</summary>
    private static readonly Regex AnonymisedCode = new("^[A-Z][0-9][A-Z0-9]+$", RegexOptions.Compiled);

    public static bool IsAnonymisedCode(string? code) => code is not null && AnonymisedCode.IsMatch(code);

    // ------------------------------------------------------------ connection

    public string? ConfigurationError =>
        string.IsNullOrWhiteSpace(_connectionString)
            ? "No connection string. Set the TARIFFHUB_DB environment variable, or copy Secrets.config.example to Secrets.config and fill it in."
            : _connectionString!.Contains("YOUR_HOST") || _connectionString.Contains("YOUR_PASSWORD")
                ? "Secrets.config still holds the placeholder connection string. Replace YOUR_HOST, YOUR_DATABASE, YOUR_USER and YOUR_PASSWORD, or set TARIFFHUB_DB."
                : null;

    private NpgsqlConnection Open() => new(_connectionString);

    // --------------------------------------------------------- filter options

    public sealed record OptionRow(string K, string V, string C);

    // ------------------------------------------------------- dropdown cache
    //
    // The dropdown lists only change when rates are loaded, but building them scans the whole table.
    // They are built once per mode and kept, together with the table's change counter from
    // pg_stat_user_tables (rows inserted + updated + deleted). Every request reads that counter - an instant
    // lookup - and rebuilds only when it has moved, so a load script is picked up without restarting the app.
    // A cached list is also rebuilt after CacheMaxAge, in case the statistics are unavailable or reset.

    private sealed record Cached(long? Version, DateTime BuiltUtc, object Value);
    private readonly ConcurrentDictionary<string, Cached> _cache = new();
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromMinutes(30);

    private const string VersionSql = """
        SELECT n_tup_ins + n_tup_upd + n_tup_del FROM pg_stat_user_tables WHERE relid = to_regclass(@Table)
        """;

    private T GetCached<T>(string key, TariffMode mode, Func<NpgsqlConnection, T> build) where T : class
    {
        using var conn = Open();
        var version = conn.ExecuteScalar<long?>(VersionSql, new { Table = TableName(mode) });
        if (_cache.TryGetValue(key, out var c) && c.Version == version && DateTime.UtcNow - c.BuiltUtc < CacheMaxAge
            && c.Value is T hit)
            return hit;

        var value = build(conn);
        _cache[key] = new Cached(version, DateTime.UtcNow, value);
        return value;
    }

    /// <summary>
    /// Distinct values per filter with the carriers that use them. Keys: 'carrier', 'ocountry', 'dcountry',
    /// 'unzoned', plus each filter key. Everything except 'carrier' is scoped to the chosen carrier when one is
    /// given. Built once per mode (see the dropdown cache); the carrier scope is applied in memory.
    /// </summary>
    public List<OptionRow> FilterOptions(TariffMode mode, string? carrier)
    {
        var all = GetCached($"options|{mode}", mode, conn => conn.Query<OptionRow>(FilterOptionsSql(mode)).ToList());
        if (carrier is null) return new List<OptionRow>(all);
        // 'unzoned' rows carry the carrier in V (C is the country); every other key carries it in C.
        return all.Where(o => o.K == "carrier" || (o.K == "unzoned" ? o.V == carrier : o.C == carrier)).ToList();
    }

    /// <summary>One statement, every carrier: ~one aggregate per dropdown, each a scan of the table.</summary>
    private string FilterOptionsSql(TariffMode mode)
    {
        var cols = Columns(mode);
        var t = TableName(mode);

        var parts = new List<string>
        {
            $"SELECT 'carrier' AS k, f.carrier_code AS v, f.carrier_code AS c FROM {t} f WHERE f.carrier_code IS NOT NULL GROUP BY 2, 3",
            // Countries reachable directly (FREIGHT) or through a zone chart (ZONE_MAP).
            $"SELECT 'ocountry', f.origin_country_code, f.carrier_code FROM {t} f WHERE f.record_type IN ('FREIGHT','ZONE_MAP') AND f.origin_country_code IS NOT NULL GROUP BY 2, 3",
            $"SELECT 'dcountry', f.dest_country_code, f.carrier_code FROM {t} f WHERE f.record_type IN ('FREIGHT','ZONE_MAP') AND f.dest_country_code IS NOT NULL GROUP BY 2, 3",
            // Zone-priced carriers with no zone chart at all: no country or place can ever reach their zones.
            // (c = the country the carrier's lanes are in, so the page only mentions it for that country)
            $"SELECT 'unzoned', f.carrier_code, COALESCE(f.origin_country_code, f.dest_country_code)::text FROM {t} f WHERE f.record_type = 'FREIGHT' AND 'ZONE' IN (f.origin_point_type, f.dest_point_type) " +
            $"AND NOT EXISTS (SELECT 1 FROM {t} z WHERE z.record_type = 'ZONE_MAP' AND z.carrier_code = f.carrier_code)" +
            (UsesZoneMaster(mode) ? " AND NOT EXISTS (SELECT 1 FROM tariff.air_zone_master zm WHERE zm.carrier_id = f.carrier_id)" : "") +
            " GROUP BY 2, 3",
        };
        // Countries reachable through an air zone chart (tariff.air_zone_master).
        if (UsesZoneMaster(mode))
        {
            parts.Add("SELECT 'ocountry', zm.origin_country_code::text, zm.carrier_code FROM tariff.air_zone_master zm WHERE zm.status = 'ACTIVE' GROUP BY 2, 3");
            parts.Add("SELECT 'dcountry', zm.dest_country_code::text, zm.carrier_code FROM tariff.air_zone_master zm WHERE zm.status = 'ACTIVE' GROUP BY 2, 3");
        }
        foreach (var def in Filters)
        {
            if (FilterColumn(def, cols) is not { } col) continue;
            var notAny = def.AnyValue is null ? "" : $" AND f.{col}::text <> '{def.AnyValue}'";
            parts.Add($"SELECT '{def.Key}', f.{col}::text, f.carrier_code FROM {t} f " +
                      $"WHERE f.record_type = 'FREIGHT' AND f.status = 'ACTIVE' AND f.{col} IS NOT NULL AND f.{col}::text <> ''{notAny} GROUP BY 2, 3");
        }

        // Lane rates stored once for every service: their services are the ones air_transit lists.
        if (UsesTransit(mode))
            parts.Add("SELECT 'service', x.service_code, x.carrier_code FROM tariff.air_transit x GROUP BY 2, 3");

        return string.Join("\nUNION ALL\n", parts) + "\nORDER BY 1, 2";
    }

    public sealed record PlaceRow(string Pt, string V, string C);
    public sealed record CountryPlaceRow(string Country, string Pt, string V, string C);

    /// <summary>
    /// Places named by FREIGHT lanes at one end ("origin" or "dest") inside a country: cities, postcodes,
    /// regions and provinces, with the carrier that uses each. Scoped to the carrier when one is chosen.
    /// Built once per mode and side for every country (see the dropdown cache); filtered in memory.
    /// </summary>
    public List<PlaceRow> Places(TariffMode mode, string? carrier, string side, string country)
    {
        if (side is not ("origin" or "dest")) throw new ArgumentOutOfRangeException(nameof(side));
        var cols = Columns(mode);
        var whens = PlaceColumnSuffix.Where(kv => cols.Contains($"{side}_{kv.Value}"))
                                     .Select(kv => $"WHEN '{kv.Key}' THEN f.{side}_{kv.Value}::text").ToList();
        if (whens.Count == 0) return new();

        var sql = $"""
            SELECT x.country, x.pt, x.v, x.c FROM (
                SELECT f.{side}_country_code::text AS country, f.{side}_point_type AS pt,
                       CASE f.{side}_point_type {string.Join(" ", whens)} END AS v, f.carrier_code AS c
                FROM {TableName(mode)} f
                WHERE f.record_type = 'FREIGHT' AND f.status = 'ACTIVE' AND f.{side}_country_code IS NOT NULL
            ) x
            WHERE x.v IS NOT NULL AND trim(x.v) <> ''
            GROUP BY 1, 2, 3, 4
            """;
        var all = GetCached($"places|{mode}|{side}", mode, conn => conn.Query<CountryPlaceRow>(sql).ToList());
        return all.Where(r => r.Country == country && (carrier is null || r.C == carrier))
                  .Select(r => new PlaceRow(r.Pt, r.V, r.C)).ToList();
    }

    // ----------------------------------------------------------------- search

    public const int MaxResults = 500;

    private static readonly string[] FlatBases = ["PER_SHIPMENT", "PER_TRUCK", "PER_CONTAINER"];

    /// <summary>rate_value is a percentage for PCT_ bases. PCT_OF_VALUE needs a goods value the search does not ask for.</summary>
    private static readonly string[] PercentBases = ["PCT_OF_FREIGHT"];

    private static string SqlList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    /// <summary>
    /// Amount of a SURCHARGE / FUEL_RULE row <paramref name="r"/> against the priced freight row p, held between
    /// the row's min_charge and max_charge. Null when the basis is unknown or the input it needs is missing — never zero.
    /// </summary>
    private static string ChargeAmountSql(string r, string kmExpr) => $"""
        (SELECT CASE WHEN a.amt IS NULL THEN NULL ELSE LEAST(GREATEST(a.amt, {r}.min_charge), {r}.max_charge) END
         FROM (SELECT CASE
                   WHEN {r}.charge_basis IN ({SqlList(PercentBases)}) THEN {r}.rate_value / 100 * p.base_freight
                   WHEN {r}.charge_basis = 'PER_KG'    THEN {r}.rate_value * p.chargeable_kg
                   WHEN {r}.charge_basis = 'PER_100KG' THEN {r}.rate_value * p.chargeable_kg / 100
                   WHEN {r}.charge_basis = 'PER_KM'    THEN {r}.rate_value * {kmExpr}
                   WHEN {r}.charge_basis IN ({SqlList(FlatBases)}) THEN {r}.rate_value
               END AS amt) a)
        """;

    /// <summary>ACTIVE and in force on the ship date (DRAFT and INACTIVE rows never price).</summary>
    private static string ValiditySql(string a) =>
        $"{a}.status = 'ACTIVE' AND {a}.valid_from <= @ShipDate::date AND ({a}.valid_to IS NULL OR {a}.valid_to >= @ShipDate::date)";

    /// <summary>The place value of one lane end for its point type (the zone for ZONE lanes).</summary>
    private static string LanePlaceSql(string side, HashSet<string> cols)
    {
        var whens = PlaceColumnSuffix
            .Where(kv => cols.Contains($"{side}_{kv.Value}"))
            .Select(kv => $"WHEN '{kv.Key}' THEN p.{side}_{kv.Value}::text")
            .Append($"WHEN 'ZONE' THEN p.{side}_region::text");
        return $"CASE p.{side}_point_type {string.Join(" ", whens)} END";
    }

    /// <summary>
    /// WHERE fragment for one end of the lane. A lane priced more coarsely than the user's place still answers
    /// for it: a COUNTRY lane covers every place in its country, and a ZONE lane covers the countries its
    /// carrier's ZONE_MAP ties to the zone. Finer lanes answer only for their own place: CITY by name (or a
    /// named warehouse by pattern), PROVINCE by any spelling of the chosen subdivision, REGION through the
    /// carrier district map (or by name), POSTCODE by prefix of the user's postcode.
    /// With only a country, every lane in that country matches ("anywhere in the country").
    /// ZONE lanes of a carrier without a zone chart are excluded whenever any country is named: nothing
    /// proves which countries the zone covers.
    /// </summary>
    private static string? SideSql(string side, string t, PlaceFilter pf, bool anyCountry, HashSet<string> cols, DynamicParameters p,
                                   bool zoneMaster)
    {
        var pt = $"f.{side}_point_type";
        var P = side == "origin" ? "Origin" : "Dest";

        if (!anyCountry) return null;

        var zoneMapped = $"""
            EXISTS (SELECT 1 FROM {t} z
                    WHERE z.record_type = 'ZONE_MAP'
                      AND z.status = 'ACTIVE'
                      AND z.carrier_code = f.carrier_code
                      AND z.service_type IS NOT DISTINCT FROM f.service_type
                      AND z.lane_type IS NOT DISTINCT FROM f.lane_type
                      AND z.{side}_region = f.{side}_region
                      AND (@OriginCountry::text IS NULL OR z.origin_country_code = @OriginCountry::text)
                      AND (@DestCountry::text IS NULL OR z.dest_country_code = @DestCountry::text))
            """;
        // Zone-priced air carriers keep their chart in tariff.air_zone_master: country pair -> rate_group of the rates.
        if (zoneMaster)
            zoneMapped = $"""
                ({zoneMapped}
                 OR EXISTS (SELECT 1 FROM tariff.air_zone_master zm
                            WHERE zm.carrier_id = f.carrier_id
                              AND zm.rate_group = f.rate_group
                              AND (zm.service_type IS NULL OR zm.service_type = f.service_type)
                              AND zm.status = 'ACTIVE'
                              AND zm.valid_from <= @ShipDate::date AND (zm.valid_to IS NULL OR zm.valid_to >= @ShipDate::date)
                              AND (@OriginCountry::text IS NULL OR zm.origin_country_code = @OriginCountry::text)
                              AND (@DestCountry::text IS NULL OR zm.dest_country_code = @DestCountry::text)))
                """;

        if (pf.Country is null)
            return $"({pt} IS DISTINCT FROM 'ZONE' OR {zoneMapped})";

        var inCountry = $"f.{side}_country_code = @{P}Country";
        if (!pf.HasPlace)
            return $"(({inCountry} AND {pt} IS DISTINCT FROM 'ZONE') OR ({pt} = 'ZONE' AND {zoneMapped}))";

        var alts = new List<string>
        {
            $"({pt} = 'COUNTRY' AND {inCountry})",
            $"({pt} = 'ZONE' AND {zoneMapped})",
        };
        if (cols.Contains($"{side}_city"))
        {
            if (pf.CityValue is not null)
            {
                p.Add($"{P}City", pf.CityValue);
                alts.Add($"({pt} = 'CITY' AND {inCountry} AND upper(trim(f.{side}_city)) = upper(trim(@{P}City)))");
            }
            if (pf.LocationPattern is not null)
            {
                p.Add($"{P}LocPattern", pf.LocationPattern);
                alts.Add($"({pt} = 'CITY' AND {inCountry} AND f.{side}_city ~* @{P}LocPattern)");
            }
        }
        if (pf.StateSpellings.Length > 0 && cols.Contains($"{side}_state"))
        {
            p.Add($"{P}States", pf.StateSpellings);
            alts.Add($"({pt} = 'PROVINCE' AND {inCountry} AND upper(trim(f.{side}_state)) = ANY(@{P}States))");
        }
        if (pf.DistrictCarriers.Length > 0)
        {
            p.Add($"{P}DCarriers", pf.DistrictCarriers);
            p.Add($"{P}DZones", pf.DistrictZones);
            alts.Add($"({pt} = 'REGION' AND {inCountry} AND (f.carrier_code::text, f.{side}_region::text) IN " +
                     $"(SELECT d.c, d.z FROM unnest(@{P}DCarriers::text[], @{P}DZones::text[]) AS d(c, z)))");
        }
        if (pf.RegionValue is not null)
        {
            p.Add($"{P}Region", pf.RegionValue);
            alts.Add($"({pt} = 'REGION' AND {inCountry} AND upper(trim(f.{side}_region)) = upper(trim(@{P}Region)))");
        }
        if (pf.AirportValue is not null && cols.Contains($"{side}_airport"))
        {
            p.Add($"{P}Airport", pf.AirportValue);
            alts.Add($"({pt} = 'AIRPORT' AND {inCountry} AND upper(trim(f.{side}_airport)) = upper(trim(@{P}Airport)))");
        }
        if (pf.Postcode is not null && cols.Contains($"{side}_postcode"))
        {
            p.Add($"{P}Postcode", pf.Postcode);
            // Prefix match: the user's postcode must start with the lane's postcode.
            alts.Add($"({pt} = 'POSTCODE' AND {inCountry} AND f.{side}_postcode IS NOT NULL " +
                     $"AND upper(replace(@{P}Postcode, ' ', '')) LIKE upper(replace(f.{side}_postcode, ' ', '')) || '%')");
        }
        return "(" + string.Join("\n      OR ", alts) + ")";
    }

    /// <summary>
    /// Filtered FREIGHT candidates with chargeable weight, plus eff_service / eff_transit. A lane rate stored once for
    /// every service (service_type empty) is joined to tariff.air_transit and comes out once per service with its hours;
    /// rows that name their own service pass through unchanged. Shared by search and the adder check.
    /// </summary>
    private static string CandidatesCte(SearchQuery q, PlaceFilter origin, PlaceFilter dest, string t, HashSet<string> cols,
                                        DynamicParameters p, bool transit, bool zoneMaster)
    {
        var where = new List<string> { "f.record_type = 'FREIGHT'", ValiditySql("f") };
        var svc = transit ? "COALESCE(f.service_type, tr.service_code)" : "f.service_type";

        if (q.Carrier is not null) where.Add("f.carrier_code = @Carrier");
        foreach (var def in Filters)
        {
            if (q.GetFilter(def.Key) is not { } value || FilterColumn(def, cols) is not { } col) continue;
            p.Add($"F_{def.Key}", value);
            var colExpr = def.Key == "service" ? svc : $"f.{col}";
            var match = $"{colExpr}::text = @F_{def.Key}";
            if (def.AnyValue is not null) match += $" OR f.{col}::text = '{def.AnyValue}'";
            if (def.NullMatchesAll) match += $" OR f.{col} IS NULL";
            where.Add($"({match})");
        }

        var anyCountry = origin.Country is not null || dest.Country is not null;
        foreach (var s in new[]
                 {
                     SideSql("origin", t, origin, anyCountry, cols, p, zoneMaster),
                     SideSql("dest", t, dest, anyCountry, cols, p, zoneMaster),
                 })
            if (s is not null) where.Add(s);

        // Air states the volume factor either as kg per m³ or as a cm³-per-kg divisor (5000 = 200 kg/m³).
        var kgPerCbm = cols.Contains("volumetric_divisor")
            ? "COALESCE(f.volumetric_kg_per_cbm, 1000000 / NULLIF(f.volumetric_divisor, 0))"
            : "f.volumetric_kg_per_cbm";

        var transitOwn = cols.Contains("transit_time") ? "f.transit_time::text" : "NULL::text";
        var eff = transit
            ? $"{svc} AS eff_service, COALESCE(tr.transit_hours::text || ' hrs', {transitOwn}) AS eff_transit"
            : $"f.service_type AS eff_service, {transitOwn} AS eff_transit";
        var join = transit
            ? """
              LEFT JOIN tariff.air_transit tr
                     ON f.service_type IS NULL
                    AND tr.carrier_id = f.carrier_id
                    AND tr.origin_airport = f.origin_airport AND tr.dest_airport = f.dest_airport
                    AND tr.service_level IS NOT DISTINCT FROM f.service_level
                    AND tr.valid_from <= @ShipDate::date AND (tr.valid_to IS NULL OR tr.valid_to >= @ShipDate::date)
              """
            : "";

        // Chargeable weight. No weight and no volume means no chargeable weight — not the minimum.
        // A rate with a weight step (express: 0.5 kg) bills the weight rounded up to the step: 30.7 kg -> 31.0 kg.
        var rawKg = $"GREATEST(@Kg::numeric, @Cbm::numeric * {kgPerCbm}, f.min_chargeable_weight_kg)";
        var stepped = cols.Contains("weight_step_kg")
            ? $"CASE WHEN f.weight_step_kg IS NULL THEN {rawKg} ELSE ceil({rawKg} / f.weight_step_kg) * f.weight_step_kg END"
            : rawKg;
        return $"""
            cand AS (
                SELECT f.*, {eff},
                       CASE WHEN @Kg::numeric IS NULL AND @Cbm::numeric IS NULL THEN NULL
                            ELSE {stepped}
                       END AS chargeable_kg
                FROM {t} f
                {join}
                WHERE {string.Join("\n  AND ", where)}
            )
            """;
    }

    private static DynamicParameters BaseParameters(SearchQuery q, PlaceFilter origin, PlaceFilter dest)
    {
        var p = new DynamicParameters();
        p.Add("ShipDate", (q.ShipDate ?? DateTime.Today).Date, DbType.Date);
        p.Add("Carrier", q.Carrier);
        p.Add("OriginCountry", origin.Country, DbType.String);
        p.Add("DestCountry", dest.Country, DbType.String);
        p.Add("Kg", q.WeightKg, DbType.Decimal);
        p.Add("Cbm", q.EffectiveVolumeCbm, DbType.Decimal);
        p.Add("Km", q.DistanceKm, DbType.Decimal);
        return p;
    }

    public (List<SearchResultRow> Rows, bool Truncated) Search(SearchQuery q, PlaceFilter origin, PlaceFilter dest)
    {
        var cols = Columns(q.Mode);
        var t = TableName(q.Mode);
        var p = BaseParameters(q, origin, dest);
        var hasDistance = HasDistance(cols);
        var km = hasDistance ? "@Km::numeric" : "NULL::numeric";

        // Bands are (from exclusive, to inclusive]; a null upper bound means no limit.
        // With no weight (or distance) entered, every band is listed but none is priced.
        var bandMatch = """
            (c.chargeable_kg IS NULL OR ((c.weight_from_kg IS NULL OR c.chargeable_kg > c.weight_from_kg)
                                     AND (c.weight_to_kg IS NULL OR c.chargeable_kg <= c.weight_to_kg)))
            """;
        var weightKnown = "(c.chargeable_kg IS NOT NULL OR (c.weight_from_kg IS NULL AND c.weight_to_kg IS NULL))";
        var distanceKnown = "TRUE";
        if (hasDistance)
        {
            bandMatch += """
                 AND (@Km::numeric IS NULL OR ((c.distance_from_km IS NULL OR @Km::numeric > c.distance_from_km)
                                          AND (c.distance_to_km IS NULL OR @Km::numeric <= c.distance_to_km)))
                """;
            distanceKnown = "(@Km::numeric IS NOT NULL OR (c.distance_from_km IS NULL AND c.distance_to_km IS NULL))";
        }

        // Lane detail that tells rows apart on screen; NULL where this mode's table has no such column.
        string Opt(string c) => cols.Contains(c) ? $"p.{c}::text AS {c}" : $"NULL::text AS {c}";
        var detailCols = string.Join(", ", new[]
        {
            "service_name", "service_level", "piece_type", "rate_group",
            "origin_region", "origin_city", "origin_airport", "dest_region", "dest_city", "dest_airport",
        }.Select(Opt));

        var levelOrder = cols.Contains("service_level") ? "p.service_level NULLS LAST, " : "";

        // Index / published fuel (tariff.fuel_charge) for carriers with a carrier_fuel_rule; a FUEL_RULE row in the
        // rate table still wins where a carrier has one.
        var fuelFn = UsesFuelFunction(q.Mode);
        var fxJoin = fuelFn
            ? """
              LEFT JOIN LATERAL tariff.fuel_charge(p.carrier_id, @ShipDate::date,
                    COALESCE(p.origin_country_code::text, @OriginCountry::text)::char(2), p.currency_code::char(3),
                    p.chargeable_kg, @Kg::numeric, p.base_freight) fx ON TRUE
              """
            : "";
        var fxFound = fuelFn ? "(fx.note IS NOT NULL)" : "FALSE";
        var fxAmount = fuelFn ? "fx.amount" : "NULL::numeric";
        var fxNote = fuelFn ? "fx.note" : "NULL::text";

        var distanceCols = hasDistance
            ? "p.distance_from_km, p.distance_to_km"
            : "NULL::numeric AS distance_from_km, NULL::numeric AS distance_to_km";

        var sql = $"""
            WITH {CandidatesCte(q, origin, dest, t, cols, p, UsesTransit(q.Mode), UsesZoneMaster(q.Mode))},
            banded AS (
                SELECT c.*,
                       CASE WHEN {weightKnown} AND {distanceKnown} THEN
                           CASE
                               WHEN c.charge_basis = 'PER_KG'    THEN c.rate_value * c.chargeable_kg
                               WHEN c.charge_basis = 'PER_100KG' THEN c.rate_value * c.chargeable_kg / 100
                               WHEN c.charge_basis = 'PER_KM'    THEN c.rate_value * {km}
                               WHEN c.charge_basis IN ({SqlList(FlatBases)}) THEN c.rate_value
                           END
                       END AS raw_freight
                FROM cand c
                WHERE {bandMatch}
            ),
            priced AS (
                -- GREATEST ignores NULL: without the CASE an unpriced row would show min_charge (or 0).
                SELECT b.*,
                       CASE WHEN b.raw_freight IS NULL THEN NULL ELSE GREATEST(b.raw_freight, b.min_charge) END AS base_freight
                FROM banded b
            ),
            totalled AS (
                SELECT p.*,
                       sc.amount AS surcharge_amount, sc.codes AS surcharge_codes,
                       COALESCE(sc.unpriced, 0) AS surcharge_unpriced, COALESCE(sc.other_ccy, 0) AS surcharge_other_currency,
                       (COALESCE(fu.found, FALSE) OR {fxFound}) AS has_fuel_rule,
                       CASE WHEN fu.found THEN fu.amt ELSE {fxAmount} END AS fuel_amount,
                       CASE WHEN fu.found THEN NULL ELSE {fxNote} END AS fuel_note,
                       CASE WHEN p.base_freight IS NULL
                              OR COALESCE(sc.unpriced, 0) > 0
                              OR COALESCE(sc.other_ccy, 0) > 0
                              OR (fu.found AND fu.amt IS NULL)
                              OR (fu.found IS NULL AND {fxFound} AND {fxAmount} IS NULL)
                            THEN NULL
                            ELSE p.base_freight + COALESCE(sc.amount, 0) + COALESCE(CASE WHEN fu.found THEN fu.amt ELSE {fxAmount} END, 0)
                       END AS estimated_total
                FROM priced p
                LEFT JOIN LATERAL (
                    SELECT sum(x.amt) FILTER (WHERE NOT x.other_ccy) AS amount,
                           count(*) FILTER (WHERE x.amt IS NULL AND NOT x.other_ccy)::int AS unpriced,
                           count(*) FILTER (WHERE x.other_ccy)::int AS other_ccy,
                           string_agg(DISTINCT x.charge_code, ', ') AS codes
                    FROM (
                        SELECT s.charge_code,
                               (s.currency_code IS NOT NULL AND s.currency_code <> p.currency_code
                                AND s.charge_basis NOT IN ({SqlList(PercentBases)})) AS other_ccy,
                               {ChargeAmountSql("s", km)} AS amt
                        FROM {t} s
                        WHERE s.record_type = 'SURCHARGE'
                          AND s.auto_apply
                          AND s.carrier_code = p.carrier_code
                          AND (s.service_type IS NULL OR s.service_type = p.eff_service)
                          AND (s.lane_type IS NULL OR s.lane_type = p.lane_type)
                          AND (s.lane_code IS NULL OR s.lane_code = p.lane_code)
                          AND (s.applies_to_cargo IS NULL OR s.applies_to_cargo = p.cargo_category)
                          AND {ValiditySql("s")}
                    ) x
                ) sc ON TRUE
                LEFT JOIN LATERAL (
                    SELECT TRUE AS found,
                           CASE WHEN r.currency_code IS NOT NULL AND r.currency_code <> p.currency_code
                                     AND r.charge_basis NOT IN ({SqlList(PercentBases)})
                                THEN NULL
                                ELSE {ChargeAmountSql("r", km)}
                           END AS amt
                    FROM {t} r
                    WHERE r.record_type = 'FUEL_RULE'
                      AND r.carrier_code = p.carrier_code
                      AND (r.service_type IS NULL OR r.service_type = p.eff_service)
                      AND {ValiditySql("r")}
                    ORDER BY (r.service_type IS NULL), r.valid_from DESC NULLS LAST
                    LIMIT 1
                ) fu ON TRUE
                {fxJoin}
            )
            SELECT p.carrier_code, p.eff_service AS service_type, p.lane_code, p.lane_type,
                   p.origin_point_type, p.origin_country_code::text AS origin_country_code, {LanePlaceSql("origin", cols)} AS origin_place,
                   p.dest_point_type, p.dest_country_code::text AS dest_country_code, {LanePlaceSql("dest", cols)} AS dest_place,
                   {detailCols}, p.eff_transit AS transit_time,
                   p.weight_from_kg, p.weight_to_kg, {distanceCols},
                   p.rate_value AS rate, p.charge_basis, p.min_charge, p.chargeable_kg, p.raw_freight, p.base_freight,
                   p.surcharge_amount, p.surcharge_codes, p.surcharge_unpriced, p.surcharge_other_currency,
                   p.has_fuel_rule, p.fuel_amount, p.fuel_note, p.estimated_total,
                   p.currency_code AS currency, p.valid_from, p.valid_to,
                   p.source_ref::text AS source_ref, p.source_sheet::text AS source_sheet, p.source_row::text AS source_row
            FROM totalled p
            -- Currencies are never compared: group by currency, then by service so each service is listed together,
            -- cheapest first within each service, unpriced last.
            ORDER BY p.currency_code, p.eff_service NULLS LAST, {levelOrder} p.estimated_total NULLS LAST, p.base_freight NULLS LAST,
                     p.carrier_code, p.lane_code, p.weight_from_kg NULLS FIRST
            LIMIT {MaxResults + 1}
            """;

        using var conn = Open();
        var rows = conn.Query<SearchResultRow>(sql, p).ToList();
        var truncated = rows.Count > MaxResults;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return (rows, truncated);
    }

    private volatile Tuple<DateTime, Dictionary<string, AirportInfo>>? _airports;

    /// <summary>
    /// Airport names from tariff.airport by IATA code, so the screen can show "DLC - Zhoushuizi Airport, Dalian"
    /// instead of the bare code. Empty while the table is missing; re-read after <see cref="CacheMaxAge"/>.
    /// </summary>
    public Dictionary<string, AirportInfo> Airports()
    {
        var cur = _airports;
        if (cur is not null && DateTime.UtcNow - cur.Item1 < CacheMaxAge) return cur.Item2;
        var map = new Dictionary<string, AirportInfo>(StringComparer.OrdinalIgnoreCase);
        if (TableExists("tariff.airport"))
        {
            using var conn = Open();
            foreach (var a in conn.Query<AirportInfo>(
                         "SELECT iata_code::text AS Code, airport_name AS Name, city_name AS City, country_code::text AS Country FROM tariff.airport"))
                map[a.Code.Trim()] = a;
        }
        _airports = Tuple.Create(DateTime.UtcNow, map);
        return map;
    }

    private readonly ConcurrentDictionary<string, bool> _tables = new();

    /// <summary>True once the table exists; re-checked until it does, so installing a table later needs no restart.</summary>
    private bool TableExists(string table)
    {
        if (_tables.TryGetValue(table, out var yes) && yes) return true;
        using var conn = Open();
        var found = conn.ExecuteScalar<bool>("SELECT to_regclass(@T) IS NOT NULL", new { T = table });
        _tables[table] = found;
        return found;
    }

    /// <summary>Fuel worked out by tariff.fuel_charge (jet fuel index or keyed-in published %), when installed.</summary>
    private bool UsesFuelFunction(TariffMode mode) => mode == TariffMode.Air && TableExists("tariff.carrier_fuel_rule");

    /// <summary>Zone-priced air carriers are matched to countries through tariff.air_zone_master.</summary>
    private bool UsesZoneMaster(TariffMode mode) => mode == TariffMode.Air && TableExists("tariff.air_zone_master");

    /// <summary>Air lane rates stored once for every service are expanded through tariff.air_transit.</summary>
    private bool UsesTransit(TariffMode mode) => mode == TariffMode.Air && TableExists("tariff.air_transit");

    /// <summary>Which accessorial sides each service carries (tariff.service_type). Empty when the table is missing.</summary>
    public Dictionary<string, (bool Origin, bool Destination)> ServiceRules()
    {
        if (!TableExists("tariff.service_type")) return new();
        using var conn = Open();
        return conn.Query<(string Code, bool Origin, bool Destination)>(
                "SELECT service_code, includes_origin, includes_destination FROM tariff.service_type")
            .ToDictionary(r => r.Code, r => (r.Origin, r.Destination));
    }


    /// <summary>
    /// Active accessorial charges (tariff.air_accessorial, named by tariff.charge_type) of the given carriers and
    /// services, in force on the ship date. Air only; empty when the table is not installed. Matching a charge to
    /// a result row's lane is done by the caller.
    /// </summary>
    public List<AccessorialRow> Accessorials(TariffMode mode, IEnumerable<string> carriers, IEnumerable<string> services, DateTime shipDate)
    {
        var c = carriers.Distinct().ToArray();
        var sv = services.Distinct().ToArray();
        if (mode != TariffMode.Air || c.Length == 0 || sv.Length == 0) return new();

        if (!TableExists("tariff.air_accessorial") || !TableExists("tariff.charge_type")) return new();
        using var conn = Open();

        const string sql = """
            SELECT a.carrier_code, a.service_type,
                   a.origin_point_type, a.origin_country_code::text AS origin_country_code, a.origin_region, a.origin_city,
                   a.origin_airport::text AS origin_airport,
                   a.dest_point_type, a.dest_country_code::text AS dest_country_code, a.dest_region, a.dest_city,
                   a.dest_airport::text AS dest_airport,
                   a.charge_code, t.charge_name, t.charge_side, a.charge_basis,
                   a.rate_value, a.min_charge, a.max_charge, a.currency_code::text AS currency_code
            FROM tariff.air_accessorial a
            JOIN tariff.charge_type t ON t.charge_code = a.charge_code
            WHERE a.carrier_code = ANY(@Carriers)
              AND (a.service_type IS NULL OR a.service_type = ANY(@Services))
              AND a.status = 'ACTIVE'
              AND a.valid_from <= @ShipDate::date AND (a.valid_to IS NULL OR a.valid_to >= @ShipDate::date)
            ORDER BY CASE t.charge_side WHEN 'ORIGIN' THEN 0 WHEN 'MAIN' THEN 1 ELSE 2 END, a.accessorial_id
            """;
        var p = new DynamicParameters();
        p.Add("Carriers", c);
        p.Add("Services", sv);
        p.Add("ShipDate", shipDate.Date, DbType.Date);
        return conn.Query<AccessorialRow>(sql, p).ToList();
    }

    /// <summary>
    /// Lanes whose bands all end below the shipment's chargeable weight, for carriers that price the
    /// excess with RULE/ADDER rows. The search deliberately does not apply adders; this only explains the gap.
    /// </summary>
    public List<AdderGapRow> AdderGaps(SearchQuery q, PlaceFilter origin, PlaceFilter dest)
    {
        if (q.WeightKg is null && q.EffectiveVolumeCbm is null) return new();

        var cols = Columns(q.Mode);
        var t = TableName(q.Mode);
        var p = BaseParameters(q, origin, dest);
        var distanceMatch = HasDistance(cols)
            ? """
              AND (@Km::numeric IS NULL OR ((c.distance_from_km IS NULL OR @Km::numeric > c.distance_from_km)
                                       AND (c.distance_to_km IS NULL OR @Km::numeric <= c.distance_to_km)))
              """
            : "";

        var sql = $"""
            WITH {CandidatesCte(q, origin, dest, t, cols, p, UsesTransit(q.Mode), UsesZoneMaster(q.Mode))}
            SELECT c.carrier_code, c.eff_service AS service_type, c.lane_code,
                   max(c.weight_to_kg) AS top_band_kg, min(c.chargeable_kg) AS chargeable_kg
            FROM cand c
            WHERE c.chargeable_kg IS NOT NULL {distanceMatch}
              AND EXISTS (SELECT 1 FROM {t} r
                          WHERE r.record_type = 'RULE' AND r.charge_code = 'ADDER' AND r.status = 'ACTIVE'
                            AND r.carrier_code = c.carrier_code)
            GROUP BY c.carrier_code, c.eff_service, c.lane_code
            HAVING bool_and(c.weight_to_kg IS NOT NULL AND c.chargeable_kg > c.weight_to_kg)
            ORDER BY 1, 2, 3
            """;

        using var conn = Open();
        return conn.Query<AdderGapRow>(sql, p).ToList();
    }
}
