namespace TariffHub.Models;

public class FilterField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public List<string> Options { get; init; } = new();
    /// <summary>Carriers that populate this column in the current mode.</summary>
    public List<string> Carriers { get; init; } = new();
    /// <summary>Set when the filter is disabled; explains why.</summary>
    public string? DisabledNote { get; init; }
    public string? Selected { get; set; }
    public bool Disabled => DisabledNote is not null;
}

public class SearchViewModel
{
    public SearchQuery Query { get; set; } = new();
    public List<string> Carriers { get; set; } = new();
    public List<FilterField> Filters { get; set; } = new();
    public List<string> OriginCountries { get; set; } = new();
    public List<string> DestCountries { get; set; } = new();
    public List<string> PlaceTypes { get; set; } = new();
    public bool SupportsPlaces { get; set; }
    public bool SupportsDistance { get; set; }

    /// <summary>Notices about inputs that were cleared or ignored, shown above the form.</summary>
    public List<string> Notices { get; set; } = new();
    /// <summary>Carrier codes that do not match the anonymised pattern; withheld from display.</summary>
    public int WithheldCarrierCodes { get; set; }
    public string? ConfigError { get; set; }

    public bool Searched { get; set; }
    public List<SearchResultRow> Results { get; set; } = new();
    public bool Truncated { get; set; }
    public List<AdderGapRow> AdderGaps { get; set; } = new();
}
