using System;
namespace TariffHub.Models;

/// <summary>One logged search: who ran it (IP / agent), what mode and filters, and how many rows came back.</summary>
public sealed class SearchAudit
{
    public long AuditId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? ClientIp { get; set; }
    public string? ForwardedFor { get; set; }
    public string? UserAgent { get; set; }
    public string? Page { get; set; }
    public string Action { get; set; } = "SEARCH";
    public string? Mode { get; set; }
    public string? Carrier { get; set; }
    public string? OriginCountry { get; set; }
    public string? OriginPlace { get; set; }
    public string? DestCountry { get; set; }
    public string? DestPlace { get; set; }
    public string? Service { get; set; }
    public DateTime? ShipDate { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? VolumeCbm { get; set; }
    public string? ShowCurrency { get; set; }
    /// <summary>Every non-empty field of the query, as a JSON string.</summary>
    public string? Filters { get; set; }
    public int? ResultCount { get; set; }
    public int? WithheldCount { get; set; }
}
