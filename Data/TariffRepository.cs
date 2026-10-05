using System.Collections.Concurrent;
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

public sealed record FilterDef(string Key, string Label, string[] ColumnCandidates);

/// <summary>
/// Every SQL string in the application lives in this file. The database is read-only from here:
/// no DDL, no DML. Column names are whitelisted constants; user input only ever travels as parameters.
/// </summary>
public sealed partial class TariffRepository
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
        "origin_point_type", "dest_point_type", "origin_country", "dest_country",
        "origin_region", "dest_region",
        "weight_from_kg", "weight_to_kg", "rate", "charge_basis", "min_charge", "currency",
        "valid_from", "valid_to", "volumetric_kg_per_cbm", "min_chargeable_weight_kg",
        "auto_apply", "charge_code", "source_ref", "source_sheet", "source_row",
    ];

    /// <summary>
    /// Optional filters. The first candidate column that exists in the table is used; a filter whose
    /// columns exist in neither table is simply not offered for that mode.
    /// </summary>
    public static readonly IReadOnlyList<FilterDef> Filters =
    [
        new("service", "Service", ["service_type"]),
        new("lanetype", "Lane type", ["lane_type"]),
        new("ratetag", "Rate tag", ["rate_tag"]),
        new("piece", "Piece type", ["piece_type"]),
        new("delivery", "Delivery address", ["delivery_address_type", "delivery_address"]),
        new("cargo", "Cargo type", ["cargo_type"]),
        new("temperature", "Temperature", ["temperature", "temperature_range", "temp_range"]),
        new("equipment", "Truck / container", ["truck_type", "container_type", "equipment_type", "vehicle_type"]),
        new("oversize", "Oversize class", ["oversize_class"]),
    ];

    /// <summary>Point type → column suffix holding the place for that type ({origin|dest}_{suffix}).</summary>
    public static readonly IReadOnlyDictionary<string, string> PlaceColumnSuffix = new Dictionary<string, string>
    {
        ["CITY"] = "city",
        ["POSTCODE"] = "postcode",
        ["REGION"] = "region",
        ["PROVINCE"] = "province",
    };

    private const string ColumnsSql = """
        SELECT column_name
        FROM information_schema.columns
        WHERE table_schema = 'tariff' AND table_name = @Table
        """;

    public async Task<HashSet<string>> ColumnsAsync(TariffMode mode)
    {
        if (_columns.TryGetValue(mode, out var cached)) return cached;

        await using var conn = Open();
        var table = mode == TariffMode.Ground ? "ground_tariff" : "air_tariff";
        var cols = (await conn.QueryAsync<string>(ColumnsSql, new { Table = table })).ToHashSet();
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
    [GeneratedRegex("^[A-Z][0-9][A-Z0-9]+$")]
    private static partial Regex AnonymisedCode();

    public static bool IsAnonymisedCode(string? code) => code is not null && AnonymisedCode().IsMatch(code);

    // ------------------------------------------------------------ connection

    public string? ConfigurationError =>
        string.IsNullOrWhiteSpace(_connectionString)
            ? "No connection string. Set the TARIFFHUB_DB environment variable, or fill in ConnectionStrings:TariffHub in appsettings.Development.json."
            : _connectionString.Contains("<HOST>") || _connectionString.Contains("<PASSWORD>")
                ? "appsettings.Development.json still holds the placeholder connection string. Replace <HOST>, <DATABASE>, <USER> and <PASSWORD>, or set TARIFFHUB_DB."
                : null;

    private NpgsqlConnection Open() => new(_connectionString);

    // --------------------------------------------------------- filter options

    public sealed record OptionRow(string K, string V, string C);

    /// <summary>
    /// Distinct values per filter with the carriers that use them, in one round trip.
    /// Keys: 'carrier', 'ocountry', 'dcountry', 'pt', plus each filter key.
    /// Everything except 'carrier' is scoped to the chosen carrier when one is given.
    /// </summary>
    public async Task<List<OptionRow>> FilterOptionsAsync(TariffMode mode, string? carrier)
    {
        var cols = await ColumnsAsync(mode);
        var t = TableName(mode);
        var scope = carrier is null ? "" : " AND f.carrier_code = @Carrier";

        var parts = new List<string>
        {
            $"SELECT 'carrier' AS k, f.carrier_code AS v, f.carrier_code AS c FROM {t} f WHERE f.carrier_code IS NOT NULL GROUP BY 2, 3",
            // Countries reachable directly (FREIGHT) or through a zone chart (ZONE_MAP).
            $"SELECT 'ocountry', f.origin_country, f.carrier_code FROM {t} f WHERE f.record_type IN ('FREIGHT','ZONE_MAP') AND f.origin_country IS NOT NULL{scope} GROUP BY 2, 3",
            $"SELECT 'dcountry', f.dest_country, f.carrier_code FROM {t} f WHERE f.record_type IN ('FREIGHT','ZONE_MAP') AND f.dest_country IS NOT NULL{scope} GROUP BY 2, 3",
            $"SELECT 'pt', f.origin_point_type, f.carrier_code FROM {t} f WHERE f.record_type = 'FREIGHT' AND f.origin_point_type IS NOT NULL{scope} GROUP BY 2, 3",
            $"SELECT 'pt', f.dest_point_type, f.carrier_code FROM {t} f WHERE f.record_type = 'FREIGHT' AND f.dest_point_type IS NOT NULL{scope} GROUP BY 2, 3",
        };
        foreach (var def in Filters)
        {
            if (FilterColumn(def, cols) is not { } col) continue;
            parts.Add($"SELECT '{def.Key}', f.{col}::text, f.carrier_code FROM {t} f " +
                      $"WHERE f.record_type = 'FREIGHT' AND f.{col} IS NOT NULL AND f.{col}::text <> ''{scope} GROUP BY 2, 3");
        }

        await using var conn = Open();
        var sql = string.Join("\nUNION ALL\n", parts) + "\nORDER BY 1, 2";
        return (await conn.QueryAsync<OptionRow>(sql, new { Carrier = carrier })).ToList();
    }

    // ----------------------------------------------------------------- search

    public const int MaxResults = 500;

    private static readonly string[] FlatBases = ["PER_SHIPMENT", "PER_TRUCK", "PER_CONTAINER"];

    /// <summary>
    /// charge_basis values treated as "percentage of base freight" for SURCHARGE and FUEL_RULE rows.
    /// UNVERIFIED against the column comments — confirm before trusting surcharge or fuel figures.
    /// </summary>
    private static readonly string[] PercentBases = ["PERCENT", "PCT", "PERCENT_OF_FREIGHT", "PCT_OF_FREIGHT"];

    private static string SqlList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    /// <summary>
    /// Amount of a SURCHARGE / FUEL_RULE row <paramref name="r"/> against the priced freight row p.
    /// Null when the basis is unknown or the input it needs is missing — never zero.
    /// </summary>
    private static string ChargeAmountSql(string r, string kmExpr) => $"""
        CASE
            WHEN {r}.charge_basis IN ({SqlList(PercentBases)}) THEN {r}.rate / 100 * p.base_freight
            WHEN {r}.charge_basis = 'PER_KG'    THEN {r}.rate * p.chargeable_kg
            WHEN {r}.charge_basis = 'PER_100KG' THEN {r}.rate * p.chargeable_kg / 100
            WHEN {r}.charge_basis = 'PER_KM'    THEN {r}.rate * {kmExpr}
            WHEN {r}.charge_basis IN ({SqlList(FlatBases)}) THEN {r}.rate
        END
        """;

    private static string ValiditySql(string a) =>
        $"({a}.valid_from IS NULL OR {a}.valid_from <= @ShipDate::date) AND ({a}.valid_to IS NULL OR {a}.valid_to >= @ShipDate::date)";

    /// <summary>Human-readable end of a lane: point type, country, and the place for that type.</summary>
    private static string LaneEndSql(string side, HashSet<string> cols)
    {
        var whens = PlaceColumnSuffix
            .Where(kv => cols.Contains($"{side}_{kv.Value}"))
            .Select(kv => $"WHEN '{kv.Key}' THEN p.{side}_{kv.Value}::text")
            .Append($"WHEN 'ZONE' THEN p.{side}_region::text");
        return $"concat_ws(' ', p.{side}_point_type, p.{side}_country, CASE p.{side}_point_type {string.Join(" ", whens)} END)";
    }

    /// <summary>
    /// WHERE fragment for one end of the lane. A lane only answers a question asked in its own terms:
    /// a place matches only lanes of that point type; a bare country matches COUNTRY lanes, or ZONE lanes
    /// whose zone the carrier's ZONE_MAP ties to the country. Zone lanes of a carrier without a zone chart
    /// are excluded whenever any country is named, because nothing proves which countries the zone covers.
    /// </summary>
    private static string? SideSql(string side, string t, string? country, string? placeType, string? place,
                                   bool anyCountry, HashSet<string> cols)
    {
        var pt = $"f.{side}_point_type";
        var P = side == "origin" ? "Origin" : "Dest";

        if (place is not null && placeType is not null && PlaceColumnSuffix.TryGetValue(placeType, out var suffix)
            && cols.Contains($"{side}_{suffix}"))
        {
            var col = $"f.{side}_{suffix}";
            var match = placeType == "POSTCODE"
                // Prefix match: the user's postcode must start with the lane's postcode.
                ? $"upper(replace(@{P}Place, ' ', '')) LIKE upper(replace({col}, ' ', '')) || '%'"
                : $"upper(trim({col})) = upper(trim(@{P}Place))";
            var countryClause = country is null ? "" : $" AND f.{side}_country = @{P}Country";
            return $"({pt} = '{placeType}' AND {col} IS NOT NULL AND {match}{countryClause})";
        }

        if (!anyCountry) return null;

        var zoneMapped = $"""
            EXISTS (SELECT 1 FROM {t} z
                    WHERE z.record_type = 'ZONE_MAP'
                      AND z.carrier_code = f.carrier_code
                      AND z.service_type IS NOT DISTINCT FROM f.service_type
                      AND z.lane_type IS NOT DISTINCT FROM f.lane_type
                      AND z.{side}_region = f.{side}_region
                      AND (@OriginCountry::text IS NULL OR z.origin_country = @OriginCountry::text)
                      AND (@DestCountry::text IS NULL OR z.dest_country = @DestCountry::text))
            """;

        return country is not null
            ? $"(({pt} = 'COUNTRY' AND f.{side}_country = @{P}Country) OR ({pt} = 'ZONE' AND {zoneMapped}))"
            : $"({pt} IS DISTINCT FROM 'ZONE' OR {zoneMapped})";
    }

    /// <summary>Filtered FREIGHT candidates (before band matching) with chargeable weight. Shared by search and the adder check.</summary>
    private static string CandidatesCte(SearchQuery q, string t, HashSet<string> cols, DynamicParameters p)
    {
        var where = new List<string> { "f.record_type = 'FREIGHT'", ValiditySql("f") };

        if (q.Carrier is not null) where.Add("f.carrier_code = @Carrier");
        foreach (var def in Filters)
        {
            if (q.GetFilter(def.Key) is not { } value || FilterColumn(def, cols) is not { } col) continue;
            p.Add($"F_{def.Key}", value);
            where.Add($"f.{col}::text = @F_{def.Key}");
        }

        var anyCountry = q.OriginCountry is not null || q.DestCountry is not null;
        foreach (var s in new[]
                 {
                     SideSql("origin", t, q.OriginCountry, q.OriginPlaceType, q.OriginPlace, anyCountry, cols),
                     SideSql("dest", t, q.DestCountry, q.DestPlaceType, q.DestPlace, anyCountry, cols),
                 })
            if (s is not null) where.Add(s);

        // Chargeable weight. No weight and no volume means no chargeable weight — not the minimum.
        return $"""
            cand AS (
                SELECT f.*,
                       CASE WHEN @Kg::numeric IS NULL AND @Cbm::numeric IS NULL THEN NULL
                            ELSE GREATEST(@Kg::numeric, @Cbm::numeric * f.volumetric_kg_per_cbm, f.min_chargeable_weight_kg)
                       END AS chargeable_kg
                FROM {t} f
                WHERE {string.Join("\n  AND ", where)}
            )
            """;
    }

    private static DynamicParameters BaseParameters(SearchQuery q)
    {
        var p = new DynamicParameters();
        p.Add("ShipDate", (q.ShipDate ?? DateTime.Today).Date, DbType.Date);
        p.Add("Carrier", q.Carrier);
        p.Add("OriginCountry", q.OriginCountry, DbType.String);
        p.Add("DestCountry", q.DestCountry, DbType.String);
        p.Add("OriginPlace", q.OriginPlace, DbType.String);
        p.Add("DestPlace", q.DestPlace, DbType.String);
        p.Add("Kg", q.WeightKg, DbType.Decimal);
        p.Add("Cbm", q.EffectiveVolumeCbm, DbType.Decimal);
        p.Add("Km", q.DistanceKm, DbType.Decimal);
        return p;
    }

    public async Task<(List<SearchResultRow> Rows, bool Truncated)> SearchAsync(SearchQuery q)
    {
        var cols = await ColumnsAsync(q.Mode);
        var t = TableName(q.Mode);
        var p = BaseParameters(q);
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

        var distanceCols = hasDistance
            ? "p.distance_from_km, p.distance_to_km"
            : "NULL::numeric AS distance_from_km, NULL::numeric AS distance_to_km";

        var sql = $"""
            WITH {CandidatesCte(q, t, cols, p)},
            banded AS (
                SELECT c.*,
                       CASE WHEN {weightKnown} AND {distanceKnown} THEN
                           CASE
                               WHEN c.charge_basis = 'PER_KG'    THEN c.rate * c.chargeable_kg
                               WHEN c.charge_basis = 'PER_100KG' THEN c.rate * c.chargeable_kg / 100
                               WHEN c.charge_basis = 'PER_KM'    THEN c.rate * {km}
                               WHEN c.charge_basis IN ({SqlList(FlatBases)}) THEN c.rate
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
                       COALESCE(fu.found, FALSE) AS has_fuel_rule, fu.amt AS fuel_amount,
                       CASE WHEN p.base_freight IS NULL
                              OR COALESCE(sc.unpriced, 0) > 0
                              OR COALESCE(sc.other_ccy, 0) > 0
                              OR (fu.found AND fu.amt IS NULL)
                            THEN NULL
                            ELSE p.base_freight + COALESCE(sc.amount, 0) + COALESCE(fu.amt, 0)
                       END AS estimated_total
                FROM priced p
                LEFT JOIN LATERAL (
                    SELECT sum(x.amt) FILTER (WHERE NOT x.other_ccy) AS amount,
                           count(*) FILTER (WHERE x.amt IS NULL AND NOT x.other_ccy)::int AS unpriced,
                           count(*) FILTER (WHERE x.other_ccy)::int AS other_ccy,
                           string_agg(DISTINCT x.charge_code, ', ') AS codes
                    FROM (
                        SELECT s.charge_code,
                               (s.currency IS NOT NULL AND s.currency <> p.currency
                                AND s.charge_basis NOT IN ({SqlList(PercentBases)})) AS other_ccy,
                               {ChargeAmountSql("s", km)} AS amt
                        FROM {t} s
                        WHERE s.record_type = 'SURCHARGE'
                          AND s.auto_apply
                          AND s.carrier_code = p.carrier_code
                          AND (s.service_type IS NULL OR s.service_type = p.service_type)
                          AND (s.lane_type IS NULL OR s.lane_type = p.lane_type)
                          AND (s.lane_code IS NULL OR s.lane_code = p.lane_code)
                          AND {ValiditySql("s")}
                    ) x
                ) sc ON TRUE
                LEFT JOIN LATERAL (
                    SELECT TRUE AS found,
                           CASE WHEN r.currency IS NOT NULL AND r.currency <> p.currency
                                     AND r.charge_basis NOT IN ({SqlList(PercentBases)})
                                THEN NULL
                                ELSE {ChargeAmountSql("r", km)}
                           END AS amt
                    FROM {t} r
                    WHERE r.record_type = 'FUEL_RULE'
                      AND r.carrier_code = p.carrier_code
                      AND (r.service_type IS NULL OR r.service_type = p.service_type)
                      AND {ValiditySql("r")}
                    ORDER BY (r.service_type IS NULL), r.valid_from DESC NULLS LAST
                    LIMIT 1
                ) fu ON TRUE
            )
            SELECT p.carrier_code, p.service_type, p.lane_code, p.lane_type,
                   {LaneEndSql("origin", cols)} AS origin_desc,
                   {LaneEndSql("dest", cols)} AS dest_desc,
                   p.weight_from_kg, p.weight_to_kg, {distanceCols},
                   p.rate, p.charge_basis, p.min_charge, p.chargeable_kg, p.raw_freight, p.base_freight,
                   p.surcharge_amount, p.surcharge_codes, p.surcharge_unpriced, p.surcharge_other_currency,
                   p.has_fuel_rule, p.fuel_amount, p.estimated_total,
                   p.currency, p.valid_from, p.valid_to,
                   p.source_ref::text AS source_ref, p.source_sheet::text AS source_sheet, p.source_row::text AS source_row
            FROM totalled p
            -- Currencies are never compared: group by currency, cheapest first within each, unpriced last.
            ORDER BY p.currency, p.estimated_total NULLS LAST, p.base_freight NULLS LAST,
                     p.carrier_code, p.lane_code, p.weight_from_kg NULLS FIRST
            LIMIT {MaxResults + 1}
            """;

        await using var conn = Open();
        var rows = (await conn.QueryAsync<SearchResultRow>(sql, p)).ToList();
        var truncated = rows.Count > MaxResults;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return (rows, truncated);
    }

    /// <summary>
    /// Lanes whose bands all end below the shipment's chargeable weight, for carriers that price the
    /// excess with RULE/ADDER rows. The search deliberately does not apply adders; this only explains the gap.
    /// </summary>
    public async Task<List<AdderGapRow>> AdderGapsAsync(SearchQuery q)
    {
        if (q.WeightKg is null && q.EffectiveVolumeCbm is null) return new();

        var cols = await ColumnsAsync(q.Mode);
        var t = TableName(q.Mode);
        var p = BaseParameters(q);
        var distanceMatch = HasDistance(cols)
            ? """
              AND (@Km::numeric IS NULL OR ((c.distance_from_km IS NULL OR @Km::numeric > c.distance_from_km)
                                       AND (c.distance_to_km IS NULL OR @Km::numeric <= c.distance_to_km)))
              """
            : "";

        var sql = $"""
            WITH {CandidatesCte(q, t, cols, p)}
            SELECT c.carrier_code, c.service_type, c.lane_code,
                   max(c.weight_to_kg) AS top_band_kg, min(c.chargeable_kg) AS chargeable_kg
            FROM cand c
            WHERE c.chargeable_kg IS NOT NULL {distanceMatch}
              AND EXISTS (SELECT 1 FROM {t} r
                          WHERE r.record_type = 'RULE' AND r.charge_code = 'ADDER'
                            AND r.carrier_code = c.carrier_code)
            GROUP BY c.carrier_code, c.service_type, c.lane_code
            HAVING bool_and(c.weight_to_kg IS NOT NULL AND c.chargeable_kg > c.weight_to_kg)
            ORDER BY 1, 2, 3
            """;

        await using var conn = Open();
        return (await conn.QueryAsync<AdderGapRow>(sql, p)).ToList();
    }
}
