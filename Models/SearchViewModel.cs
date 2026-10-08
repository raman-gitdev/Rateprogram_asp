using System.Collections.Generic;
namespace TariffHub.Models;

public class FilterField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Options { get; set; } = new();
    /// <summary>Carriers that populate this column in the current mode.</summary>
    public List<string> Carriers { get; set; } = new();
    /// <summary>True when rows without a specific value (ANY / NULL) still match a chosen value.</summary>
    public bool OthersStillMatch { get; set; }
    /// <summary>Set when the filter is disabled; explains why.</summary>
    public string? DisabledNote { get; set; }
    /// <summary>The column does not exist in this mode's table; the page hides the field.</summary>
    public bool NotInMode { get; set; }
    public string? Selected { get; set; }
    public bool Disabled => DisabledNote is not null;
}

/// <summary>One entry of a "Place" dropdown. Value is "TYPE|value", e.g. "PROVINCE|JP-13", "CITY|Shanghai".</summary>
public sealed record PlaceOption(string Value, string Label, string Group);

/// <summary>A value/label pair for a plain dropdown.</summary>
public sealed record Option(string Value, string Label);

public class SearchViewModel
{
    public SearchQuery Query { get; set; } = new();
    public List<string> Carriers { get; set; } = new();
    public List<FilterField> Filters { get; set; } = new();
    public List<Option> OriginCountries { get; set; } = new();
    public List<Option> DestCountries { get; set; } = new();
    /// <summary>Places in the chosen origin / destination country; empty until a country is chosen.</summary>
    public List<PlaceOption> OriginPlaces { get; set; } = new();
    public List<PlaceOption> DestPlaces { get; set; } = new();
    /// <summary>The table has place columns (ground); air lanes are country to country.</summary>
    public bool SupportsPlaces { get; set; }
    public bool SupportsPostcode { get; set; }
    /// <summary>Airports in the chosen origin / destination country (air), shown in their own dropdown.</summary>
    public List<Option> OriginAirports { get; set; } = new();
    public List<Option> DestAirports { get; set; } = new();
    /// <summary>The table has airport columns (air).</summary>
    public bool SupportsAirports { get; set; }
    /// <summary>Carriers priced by zone without any zone chart: they cannot be matched to a country or place.</summary>
    public List<string> UnzonedCarriers { get; set; } = new();
    public bool SupportsDistance { get; set; }

    /// <summary>Notices about inputs that were cleared or ignored, shown above the form.</summary>
    public List<string> Notices { get; set; } = new();
    /// <summary>Carrier codes that do not match the anonymised pattern; withheld from display.</summary>
    public int WithheldCarrierCodes { get; set; }
    /// <summary>Displayed values (option or cell) withheld because they contain a real party name.</summary>
    public int WithheldValues { get; set; }
    public string? ConfigError { get; set; }

    public bool Searched { get; set; }
    public List<SearchResultRow> Results { get; set; } = new();
    public bool Truncated { get; set; }
    public List<AdderGapRow> AdderGaps { get; set; } = new();
}
