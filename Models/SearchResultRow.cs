using System;
using System.Collections.Generic;
namespace TariffHub.Models;

/// <summary>One priced (or unpriceable) FREIGHT band. Column names map from snake_case SQL aliases.</summary>
public class SearchResultRow
{
    public string CarrierCode { get; set; } = "";
    public string? ServiceType { get; set; }
    public string? LaneCode { get; set; }
    public string? LaneType { get; set; }
    public string? OriginPointType { get; set; }
    public string? OriginCountryCode { get; set; }
    public string? OriginPlace { get; set; }
    public string? DestPointType { get; set; }
    public string? DestCountryCode { get; set; }
    public string? DestPlace { get; set; }
    /// <summary>Lane detail that tells otherwise identical rows apart (null where the table has no such column).</summary>
    public string? ServiceName { get; set; }
    /// <summary>Speed level (SL0 .. SL3) when the card has levels.</summary>
    public string? ServiceLevel { get; set; }
    public string? PieceType { get; set; }
    public string? RateGroup { get; set; }
    public string? TransitTime { get; set; }
    public string? OriginRegion { get; set; }
    public string? OriginCity { get; set; }
    public string? OriginAirport { get; set; }
    public string? DestRegion { get; set; }
    public string? DestCity { get; set; }
    public string? DestAirport { get; set; }
    /// <summary>Readable lane ends, built by SearchService from the three fields above and the master data.</summary>
    public string? OriginDesc { get; set; }
    public string? DestDesc { get; set; }
    /// <summary>Tooltip of the From / To cell: the full airport name for an airport lane.</summary>
    public string? OriginTitle { get; set; }
    public string? DestTitle { get; set; }
    /// <summary>Same offer appears in more than one zone because the carrier splits the country by station.</summary>
    public bool ZoneDependsOnAddress { get; set; }
    public decimal? WeightFromKg { get; set; }
    public decimal? WeightToKg { get; set; }
    public decimal? DistanceFromKm { get; set; }
    public decimal? DistanceToKm { get; set; }
    public decimal? Rate { get; set; }
    public string? ChargeBasis { get; set; }
    public decimal? MinCharge { get; set; }
    public decimal? ChargeableKg { get; set; }
    public decimal? RawFreight { get; set; }
    public decimal? BaseFreight { get; set; }
    public decimal? SurchargeAmount { get; set; }
    public string? SurchargeCodes { get; set; }
    public int SurchargeUnpriced { get; set; }
    public int SurchargeOtherCurrency { get; set; }
    public bool HasFuelRule { get; set; }
    public decimal? FuelAmount { get; set; }
    /// <summary>How the fuel was worked out, or what is missing (e.g. prices not loaded yet). From tariff.fuel_charge.</summary>
    public string? FuelNote { get; set; }
    public decimal? EstimatedTotal { get; set; }
    public string? Currency { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? SourceRef { get; set; }
    public string? SourceSheet { get; set; }
    public string? SourceRow { get; set; }

    /// <summary>Optional charges that come with this service (air_accessorial), priced for this shipment. Never in EstimatedTotal: the user picks them.</summary>
    public List<AccessorialCharge> Accessorials { get; set; } = new();

    /// <summary>Why estimated_total is null, for display. Null when a total exists.</summary>
    public string? NoTotalReason =>
        EstimatedTotal is not null ? null
        : BaseFreight is null ? NoFreightReason
        : SurchargeOtherCurrency > 0 ? "an auto surcharge is in another currency"
        : SurchargeUnpriced > 0 ? "an auto surcharge could not be priced"
        : HasFuelRule && FuelAmount is null ? FuelNote ?? "fuel rule could not be priced"
        : "not priced";

    private string NoFreightReason => ChargeBasis switch
    {
        "PER_KG" or "PER_100KG" => "enter weight or volume",
        "PER_KM" => "enter distance",
        _ => "not priced"
    };
}

/// <summary>A lane with no band covering the shipment, whose carrier prices weight above the bands with an ADDER rule.</summary>
public class AdderGapRow
{
    public string CarrierCode { get; set; } = "";
    public string? ServiceType { get; set; }
    public string? LaneCode { get; set; }
    public decimal TopBandKg { get; set; }
    public decimal ChargeableKg { get; set; }
}

/// <summary>One air_accessorial row as read from the database (lane key + charge).</summary>
public class AccessorialRow
{
    public string CarrierCode { get; set; } = "";
    /// <summary>Empty = a lane charge for every service that includes its side.</summary>
    public string? ServiceType { get; set; }
    public string? OriginPointType { get; set; }
    public string? OriginCountryCode { get; set; }
    public string? OriginRegion { get; set; }
    public string? OriginCity { get; set; }
    public string? OriginAirport { get; set; }
    public string? DestPointType { get; set; }
    public string? DestCountryCode { get; set; }
    public string? DestRegion { get; set; }
    public string? DestCity { get; set; }
    public string? DestAirport { get; set; }
    public string ChargeCode { get; set; } = "";
    public string ChargeName { get; set; } = "";
    public string ChargeSide { get; set; } = "";
    public string ChargeBasis { get; set; } = "";
    public decimal RateValue { get; set; }
    public decimal? MinCharge { get; set; }
    public decimal? MaxCharge { get; set; }
    public string CurrencyCode { get; set; } = "";
}

/// <summary>An accessorial charge priced for one result row.</summary>
public class AccessorialCharge
{
    public string ChargeCode { get; set; } = "";
    public string ChargeName { get; set; } = "";
    public string ChargeSide { get; set; } = "";
    public string ChargeBasis { get; set; } = "";
    public decimal Rate { get; set; }
    public decimal? MinCharge { get; set; }
    public decimal? MaxCharge { get; set; }
    public string Currency { get; set; } = "";
    /// <summary>Amount for this shipment after min / max; null when it cannot be priced (see <see cref="NotPricedReason"/>).</summary>
    public decimal? Amount { get; set; }
    public string? NotPricedReason { get; set; }
    /// <summary>The minimum or maximum decided the amount.</summary>
    public string? LimitApplied { get; set; }
}
