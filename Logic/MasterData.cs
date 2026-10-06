using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TariffHub.Logic;

/// <summary>A state / province / prefecture (ISO 3166-2).</summary>
public sealed record Subdivision(string Code, string CountryCode, string Name, string ShortName, string? Type, string? ParentCode);

/// <summary>A named site a carrier's lanes start or end at (warehouse); matched to tariff city values by pattern.</summary>
public sealed record CarrierLocation(string CarrierCode, string Code, string Name, string CountryCode, string? Pattern);

/// <summary>
/// Reference data read once from db/master/data/*.json (built by db/master/tools/build_master.py):
/// country names, subdivisions and their spellings, filter labels, carrier districts and warehouses.
/// Missing or unreadable files leave the affected part empty and are reported in <see cref="LoadErrors"/>;
/// the page then falls back to showing codes.
/// </summary>
public sealed class MasterData
{
    private readonly Dictionary<string, string> _countryNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Subdivision>> _subdivisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Subdivision> _subdivisionByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _subdivisionSpellings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    // subdivision code -> (carrier, zone) for carriers that price to districts made of subdivisions
    private readonly Dictionary<string, List<(string Carrier, string Zone)>> _districts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _districtCarriers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CarrierLocation> _locations = new();

    public List<string> LoadErrors { get; } = new();

    public MasterData(string folder)
    {
        Read(folder, "countries.json", root =>
        {
            foreach (var c in root.GetProperty("countries").EnumerateArray())
                _countryNames[Str(c, "country_code")!] = Str(c, "name")!;
        });
        Read(folder, "subdivisions.json", root =>
        {
            foreach (var s in root.GetProperty("subdivisions").EnumerateArray())
            {
                var sub = new Subdivision(Str(s, "subdivision_code")!, Str(s, "country_code")!, Str(s, "name")!,
                                          Str(s, "short_name")!, Str(s, "type"), Str(s, "parent_code"));
                _subdivisionByCode[sub.Code] = sub;
                if (!_subdivisions.TryGetValue(sub.CountryCode, out var list)) _subdivisions[sub.CountryCode] = list = new();
                list.Add(sub);
                AddSpelling(sub.Code, sub.Name);
                AddSpelling(sub.Code, sub.ShortName);
                AddSpelling(sub.Code, Str(s, "ascii_name"));
            }
        });
        Read(folder, "subdivision_aliases.json", root =>
        {
            foreach (var a in root.GetProperty("aliases").EnumerateArray())
                AddSpelling(Str(a, "subdivision_code")!, Str(a, "alias"));
        });
        Read(folder, "code_lists.json", root =>
        {
            foreach (var list in root.EnumerateObject().Where(p => !p.Name.StartsWith("_") && p.Value.ValueKind == JsonValueKind.Array))
                foreach (var item in list.Value.EnumerateArray())
                    _labels[list.Name + "\0" + Str(item, "code")] = Str(item, "label")!;
        });

        var carriers = Path.Combine(folder, "carriers");
        if (Directory.Exists(carriers))
            foreach (var file in Directory.GetFiles(carriers, "*.json"))
                Read(carriers, Path.GetFileName(file), ReadCarrier);
    }

    private void ReadCarrier(JsonElement root)
    {
        var carrier = Str(root, "carrier_code");
        if (carrier is null) return;

        if (root.TryGetProperty("districts", out var districts))
        {
            _districtCarriers.Add(carrier);
            foreach (var d in districts.EnumerateArray())
            {
                var zone = Str(d, "zone_code")!;
                foreach (var m in d.GetProperty("members").EnumerateArray())
                {
                    var sub = Str(m, "subdivision")!;
                    if (!_districts.TryGetValue(sub, out var list)) _districts[sub] = list = new();
                    list.Add((carrier, zone));
                }
            }
        }
        if (root.TryGetProperty("locations", out var locations))
            foreach (var l in locations.EnumerateArray())
                _locations.Add(new CarrierLocation(carrier, Str(l, "location_code")!, Str(l, "name")!,
                                                   Str(l, "country_code")!, Str(l, "tariff_place_pattern")));
    }

    private void Read(string folder, string file, Action<JsonElement> apply)
    {
        var path = Path.Combine(folder, file);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            apply(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            LoadErrors.Add($"{file}: {ex.Message}");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ").ToUpperInvariant();

    private void AddSpelling(string code, string? spelling)
    {
        if (string.IsNullOrWhiteSpace(spelling)) return;
        if (!_subdivisionSpellings.TryGetValue(code, out var set))
            _subdivisionSpellings[code] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(Norm(spelling!));
    }

    // ------------------------------------------------------------- countries

    public string CountryName(string code) => _countryNames.TryGetValue(code, out var n) ? n : code;

    /// <summary>"Japan (JP)"; the bare code when the country is unknown.</summary>
    public string CountryLabel(string code) => _countryNames.TryGetValue(code, out var n) ? $"{n} ({code})" : code;

    // ---------------------------------------------------------- subdivisions

    /// <summary>Top-level subdivisions of a country (states, provinces, prefectures), by name.</summary>
    public IReadOnlyList<Subdivision> TopSubdivisions(string countryCode) =>
        _subdivisions.TryGetValue(countryCode, out var list)
            ? list.Where(s => s.ParentCode is null).OrderBy(s => s.ShortName, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<Subdivision>();

    public Subdivision? SubdivisionByCode(string code) => _subdivisionByCode.TryGetValue(code, out var s) ? s : null;

    /// <summary>Every known spelling of a subdivision (upper case), for matching tariff state columns.</summary>
    public IReadOnlyCollection<string> SubdivisionSpellings(string code) =>
        _subdivisionSpellings.TryGetValue(code, out var set) ? set : (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>The subdivision a printed name refers to within a country ("Yunnan", "Guangdong Sheng", "Tokyo-to").</summary>
    public string? SubdivisionCodeOf(string countryCode, string name)
    {
        var n = Norm(name);
        return _subdivisions.TryGetValue(countryCode, out var list)
            ? list.FirstOrDefault(s => SubdivisionSpellings(s.Code).Contains(n))?.Code
            : null;
    }

    // ------------------------------------------------------- carrier geography

    /// <summary>Carrier districts (REGION values) that contain the subdivision, e.g. JP-40 -> (B1C06, Kyushu A).</summary>
    public IReadOnlyList<(string Carrier, string Zone)> DistrictsOf(string subdivisionCode) =>
        _districts.TryGetValue(subdivisionCode, out var list) ? list : Array.Empty<(string, string)>();

    /// <summary>True when the carrier's REGION values are districts defined by subdivisions in the master data.</summary>
    public bool HasDistrictMap(string carrierCode) => _districtCarriers.Contains(carrierCode);

    public IReadOnlyList<CarrierLocation> Locations(string countryCode) =>
        _locations.Where(l => l.CountryCode == countryCode && l.Pattern is not null).ToList();

    public CarrierLocation? Location(string code) => _locations.FirstOrDefault(l => l.Code == code);

    /// <summary>The locations a tariff city value stands for (one rate table can serve two warehouses); matched by pattern, never by name.</summary>
    public IReadOnlyList<CarrierLocation> LocationsForTariffValue(string carrierCode, string? value) =>
        value is null ? Array.Empty<CarrierLocation>()
        : _locations.Where(l => l.CarrierCode == carrierCode && l.Pattern is not null
                                && Regex.IsMatch(value, l.Pattern, RegexOptions.IgnoreCase)).ToList();

    // ---------------------------------------------------------------- labels

    /// <summary>Display label of a coded value (list = tariff column name, e.g. service_type); the code when unknown.</summary>
    public string Label(string list, string code) => _labels.TryGetValue(list + "\0" + code, out var l) ? l : code;
}
