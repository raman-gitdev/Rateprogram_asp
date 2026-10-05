namespace TariffHub.Models;

/// <summary>One priced (or unpriceable) FREIGHT band. Column names map from snake_case SQL aliases.</summary>
public class SearchResultRow
{
    public string CarrierCode { get; set; } = "";
    public string? ServiceType { get; set; }
    public string? LaneCode { get; set; }
    public string? LaneType { get; set; }
    public string? OriginDesc { get; set; }
    public string? DestDesc { get; set; }
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
    public decimal? EstimatedTotal { get; set; }
    public string? Currency { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? SourceRef { get; set; }
    public string? SourceSheet { get; set; }
    public string? SourceRow { get; set; }

    /// <summary>Why estimated_total is null, for display. Null when a total exists.</summary>
    public string? NoTotalReason =>
        EstimatedTotal is not null ? null
        : BaseFreight is null ? NoFreightReason
        : SurchargeOtherCurrency > 0 ? "an auto surcharge is in another currency"
        : SurchargeUnpriced > 0 ? "an auto surcharge could not be priced"
        : HasFuelRule && FuelAmount is null ? "fuel rule could not be priced"
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
