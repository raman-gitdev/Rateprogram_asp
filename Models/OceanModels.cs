using System;
using System.Collections.Generic;
namespace TariffHub.Models;

/// <summary>Ocean search is its own mode: FCL is priced per container (equipment type), LCL by weight/measure (W/M).
/// The form drills country -> port (-> city when a port serves several), on both ends.</summary>
public sealed class OceanQuery
{
    public string Service { get; set; } = "FCL";       // FCL | LCL
    public string Routing { get; set; } = "PRIMARY";   // PRIMARY | BACKUP
    public DateTime? ShipDate { get; set; }

    public string? Carrier { get; set; }
    public string? OriginCountry { get; set; }
    public string? OriginPort { get; set; }
    public string? OriginCity { get; set; }
    public string? DestCountry { get; set; }
    public string? DestPort { get; set; }
    public string? DestCity { get; set; }

    public string? Equipment { get; set; }             // FCL only
    public int? Containers { get; set; }               // FCL, default 1
    public decimal? WeightKg { get; set; }             // LCL
    public decimal? VolumeCbm { get; set; }            // LCL

    public bool IsFcl => string.Equals(Service, "FCL", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A port of one country: the code and how many cities feed it.</summary>
public sealed record OceanPort(string Code, int CityCount);

/// <summary>One stored charge line of an ocean lane (default trailer), as read from the database.</summary>
public sealed record OceanChargeRow(
    string OriginCity, string OriginPort, string DestCity, string DestPort,
    string Leg, string ChargeCode, string? ChargeName, string ChargeBasis,
    decimal? RateValue, decimal? MinCharge, decimal? MaxCharge);

/// <summary>Per-carrier ocean summary for the Loaded tariffs page.</summary>
public sealed class OceanLoadedRow
{
    public string CarrierCode { get; set; } = "";
    public string? Currencies { get; set; }
    public long FclRows { get; set; }
    public long LclRows { get; set; }
    public long FclLanes { get; set; }
    public long LclLanes { get; set; }
    public long OriginCountries { get; set; }
    public long DestCountries { get; set; }
    public long OriRows { get; set; }
    public long AccessorialRows { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public DateTime? LastLoaded { get; set; }
}

/// <summary>One priced charge line of an ocean quote.</summary>
public sealed class OceanLine
{
    public string Leg { get; set; } = "";              // ORIGIN | OCEAN | DESTINATION
    public string ChargeCode { get; set; } = "";
    public string? ChargeName { get; set; }
    public string ChargeBasis { get; set; } = "";      // PER_CONTAINER | PER_WM | PER_SHIPMENT
    public decimal? Rate { get; set; }
    public decimal? MinCharge { get; set; }
    public decimal? MaxCharge { get; set; }
    public decimal? Amount { get; set; }               // computed for this shipment; null = not priced
    public string? LimitApplied { get; set; }          // "min" | "max"
    public string? Note { get; set; }                  // e.g. the ORI month, or why not priced
}

public sealed class OceanQuoteResult
{
    public string CarrierCode { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public string Service { get; set; } = "FCL";
    public string Routing { get; set; } = "PRIMARY";
    public string? Equipment { get; set; }
    public string OriginDesc { get; set; } = "";
    public string DestDesc { get; set; } = "";
    public string? Basis { get; set; }                 // "1 × 40HC" or "3.40 W/M (max of 3.40 cbm, 2 400 kg)"
    public List<OceanLine> Lines { get; set; } = new();
    public decimal PortToPort { get; set; }            // ocean freight leg only (+ ORI)
    public decimal DoorToDoor { get; set; }            // everything that could be priced
    public decimal? Ori { get; set; }
    public string? OriNote { get; set; }
    public List<string> Notes { get; set; } = new();
}

/// <summary>Form state for the ocean search page: the cascading dropdowns and, after Search, one quote per matching lane.</summary>
public sealed class OceanViewModel
{
    public OceanQuery Query { get; set; } = new();
    public bool Installed { get; set; }
    public List<string> Carriers { get; set; } = new();
    public List<Option> OriginCountries { get; set; } = new();
    public List<OceanPort> OriginPorts { get; set; } = new();
    public List<string> OriginCities { get; set; } = new();
    public List<Option> DestCountries { get; set; } = new();
    public List<OceanPort> DestPorts { get; set; } = new();
    public List<string> DestCities { get; set; } = new();
    public List<string> Equipment { get; set; } = new();

    public List<string> Notices { get; set; } = new();
    public int WithheldCarrierCodes { get; set; }
    public int WithheldValues { get; set; }
    public string? ConfigError { get; set; }

    public bool Searched { get; set; }
    public List<OceanQuoteResult> Results { get; set; } = new();
}
