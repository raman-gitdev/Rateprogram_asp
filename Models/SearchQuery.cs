using System;
namespace TariffHub.Models;

public enum TariffMode { Ground, Air }

/// <summary>Form binding model for the search page. Every field is optional.</summary>
public class SearchQuery
{
    public TariffMode Mode { get; set; } = TariffMode.Ground;
    public DateTime? ShipDate { get; set; }

    public string? Carrier { get; set; }
    public string? Service { get; set; }
    public string? ServiceLevel { get; set; }
    public string? PieceType { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? LaneType { get; set; }
    public string? RateTag { get; set; }
    public string? CargoType { get; set; }
    public string? Temperature { get; set; }
    public string? Equipment { get; set; }
    public string? OversizeClass { get; set; }

    public string? OriginCountry { get; set; }
    /// <summary>CITY, POSTCODE, REGION, PROVINCE (value = ISO 3166-2 code, or the tariff spelling) or LOCATION (value = location code).</summary>
    public string? OriginPlaceType { get; set; }
    public string? OriginPlace { get; set; }
    public string? OriginPostcode { get; set; }
    /// <summary>IATA code picked in the separate Airport dropdown (air only).</summary>
    public string? OriginAirport { get; set; }
    public string? DestCountry { get; set; }
    public string? DestPlaceType { get; set; }
    public string? DestPlace { get; set; }
    public string? DestPostcode { get; set; }
    public string? DestAirport { get; set; }

    public decimal? WeightKg { get; set; }
    public decimal? VolumeCbm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public int? Pieces { get; set; }
    public decimal? DistanceKm { get; set; }

    /// <summary>Explicit volume wins; otherwise L×W×H (cm) × pieces converted to m³.</summary>
    public decimal? EffectiveVolumeCbm =>
        VolumeCbm ?? (LengthCm is { } l && WidthCm is { } w && HeightCm is { } h
            ? l * w * h / 1_000_000m * (Pieces ?? 1)
            : null);

    /// <summary>Filter value by filter key (see TariffRepository.Filters).</summary>
    public string? GetFilter(string key) => key switch
    {
        "service" => Service,
        "level" => ServiceLevel,
        "piece" => PieceType,
        "delivery" => DeliveryAddress,
        "lanetype" => LaneType,
        "ratetag" => RateTag,
        "cargo" => CargoType,
        "temperature" => Temperature,
        "equipment" => Equipment,
        "oversize" => OversizeClass,
        _ => null
    };

    public void SetFilter(string key, string? value)
    {
        switch (key)
        {
            case "service": Service = value; break;
            case "level": ServiceLevel = value; break;
            case "piece": PieceType = value; break;
            case "delivery": DeliveryAddress = value; break;
            case "lanetype": LaneType = value; break;
            case "ratetag": RateTag = value; break;
            case "cargo": CargoType = value; break;
            case "temperature": Temperature = value; break;
            case "equipment": Equipment = value; break;
            case "oversize": OversizeClass = value; break;
        }
    }
}
