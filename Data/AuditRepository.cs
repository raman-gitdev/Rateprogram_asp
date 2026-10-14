using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Npgsql;
using TariffHub.Models;

namespace TariffHub.Data;

/// <summary>
/// Writes and reads the search / usage log (tariff.search_audit, script 78). Kept apart from
/// <see cref="TariffRepository"/>, which is read-only. Logging is best-effort: a failure to write a log
/// row never blocks a search.
/// </summary>
public sealed class AuditRepository
{
    private readonly string? _connectionString;

    public AuditRepository(string? connectionString)
    {
        _connectionString = connectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private const string InsertSql = """
        INSERT INTO tariff.search_audit
          (client_ip, forwarded_for, user_agent, page, action, mode, carrier, origin_country, origin_place,
           dest_country, dest_place, service, ship_date, weight_kg, volume_cbm, show_currency, filters,
           result_count, withheld_count)
        VALUES
          (@ClientIp, @ForwardedFor, @UserAgent, @Page, @Action, @Mode, @Carrier, @OriginCountry, @OriginPlace,
           @DestCountry, @DestPlace, @Service, @ShipDate, @WeightKg, @VolumeCbm, @ShowCurrency, CAST(@Filters AS jsonb),
           @ResultCount, @WithheldCount)
        """;

    /// <summary>Logs one search. Never throws: any error (including the table not existing yet) is swallowed.</summary>
    public void Log(SearchAudit a)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Execute(InsertSql, a, commandTimeout: 5);
        }
        catch
        {
            // best-effort: logging must never break a user's search.
        }
    }

    /// <summary>The most recent logged searches (optionally one mode), newest first, for the activity page.</summary>
    public List<SearchAudit> Recent(int limit, string? mode)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return new();
        const string sql = """
            SELECT audit_id AS "AuditId", occurred_at AS "OccurredAt", client_ip AS "ClientIp",
                   forwarded_for AS "ForwardedFor", user_agent AS "UserAgent", page AS "Page", action AS "Action",
                   mode AS "Mode", carrier AS "Carrier", origin_country AS "OriginCountry", origin_place AS "OriginPlace",
                   dest_country AS "DestCountry", dest_place AS "DestPlace", service AS "Service", ship_date AS "ShipDate",
                   weight_kg AS "WeightKg", volume_cbm AS "VolumeCbm", show_currency AS "ShowCurrency",
                   filters::text AS "Filters", result_count AS "ResultCount", withheld_count AS "WithheldCount"
            FROM tariff.search_audit
            WHERE (@Mode IS NULL OR mode = @Mode)
            ORDER BY occurred_at DESC
            LIMIT @Limit
            """;
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            return conn.Query<SearchAudit>(sql, new { Limit = limit, Mode = mode }, commandTimeout: 30).ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Totals for the activity page header: all-time, today, and distinct caller IPs.</summary>
    public (long Total, long Today, long Ips) Summary()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return (0, 0, 0);
        const string sql = """
            SELECT count(*) AS "Total",
                   count(*) FILTER (WHERE occurred_at >= date_trunc('day', now())) AS "Today",
                   count(DISTINCT client_ip) AS "Ips"
            FROM tariff.search_audit
            """;
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            return conn.QueryFirst<(long Total, long Today, long Ips)>(sql, commandTimeout: 30);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
