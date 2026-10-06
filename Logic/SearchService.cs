using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using TariffHub.Data;
using TariffHub.Models;

namespace TariffHub.Logic;

/// <summary>
/// Builds the search form state (lists from the database and the master data, stale filters cleared with a note)
/// and runs the search. The page only maps controls to and from <see cref="SearchViewModel"/>.
/// </summary>
public sealed class SearchService(TariffRepository repo, Redactor redactor, MasterData master)
{
    private const string GroupLocations = "Warehouses and sites";
    private const string GroupCities = "Cities";
    private const string GroupStates = "States / provinces / prefectures";
    private const string GroupRegions = "Regions";
    private const string GroupPostcodes = "Postcodes priced by a carrier";

    /// <summary>Form state for a query. Mode, carrier or country changes reload every dependent list and clear choices that no longer apply.</summary>
    public SearchViewModel BuildForm(SearchQuery q)
    {
        Normalise(q);
        var vm = new SearchViewModel { Query = q };
        foreach (var e in master.LoadErrors) vm.Notices.Add("Master data not loaded: " + e);

        if (repo.ConfigurationError is { } configError)
        {
            vm.ConfigError = configError;
            return vm;
        }

        try
        {
            var cols = repo.Columns(q.Mode);
            var modeName = q.Mode.ToString().ToLowerInvariant();

            // Carrier first: every other list is scoped to it.
            var all = repo.FilterOptions(q.Mode, null);
            var carriers = all.Where(o => o.K == "carrier").Select(o => o.V).Distinct().ToList();
            vm.WithheldCarrierCodes = carriers.Count(c => !TariffRepository.IsAnonymisedCode(c));
            vm.Carriers = carriers.Where(TariffRepository.IsAnonymisedCode).ToList();
            if (q.Carrier is not null && !vm.Carriers.Contains(q.Carrier))
            {
                vm.Notices.Add($"Carrier cleared: {q.Carrier} has no {modeName} tariff.");
                q.Carrier = null;
            }

            var options = q.Carrier is null ? all : repo.FilterOptions(q.Mode, q.Carrier);
            // Option values naming a real party are never offered (an option's value is visible in the page source).
            var named = options.Where(o => redactor.Hits(o.V)).Select(o => o.K + "\0" + o.V).Distinct().Count();
            vm.WithheldValues += named;
            options = options.Where(o => !redactor.Hits(o.V)).ToList();
            List<string> Values(string key) => options.Where(o => o.K == key).Select(o => o.V).Distinct().ToList();
            var carrierScope = q.Carrier is null ? $"No {modeName} tariff" : $"Carrier {q.Carrier}";

            foreach (var def in TariffRepository.Filters)
            {
                var selected = q.GetFilter(def.Key);
                FilterField field;
                if (TariffRepository.FilterColumn(def, cols) is null)
                {
                    field = new FilterField { Key = def.Key, Label = def.Label, NotInMode = true, DisabledNote = $"Not part of the {modeName} tariff." };
                }
                else
                {
                    var values = Values(def.Key);
                    var users = options.Where(o => o.K == def.Key).Select(o => o.C).Distinct()
                                       .Where(TariffRepository.IsAnonymisedCode).OrderBy(c => c).ToList();
                    field = new FilterField
                    {
                        Key = def.Key, Label = def.Label, Options = values, Carriers = users,
                        OthersStillMatch = def.AnyValue is not null || def.NullMatchesAll,
                        DisabledNote = values.Count == 0 ? $"{carrierScope} has no values for this field." : null,
                    };
                }

                if (selected is not null && !field.Options.Contains(selected))
                {
                    vm.Notices.Add($"{def.Label} cleared: '{selected}' does not exist in the {modeName} tariff{(q.Carrier is null ? "" : $" for {q.Carrier}")}.");
                    q.SetFilter(def.Key, null);
                    selected = null;
                }
                field.Selected = selected;
                vm.Filters.Add(field);
            }

            // Countries: codes from the tariffs, names from the master data.
            List<Option> Countries(string key) => Values(key)
                .Select(c => new Option(c, master.CountryLabel(c)))
                .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();
            vm.OriginCountries = Countries("ocountry");
            vm.DestCountries = Countries("dcountry");
            if (q.OriginCountry is not null && vm.OriginCountries.All(o => o.Value != q.OriginCountry))
            {
                vm.Notices.Add($"From country cleared: no {modeName} tariff starts in {master.CountryName(q.OriginCountry)}.");
                q.OriginCountry = null;
            }
            if (q.DestCountry is not null && vm.DestCountries.All(o => o.Value != q.DestCountry))
            {
                vm.Notices.Add($"To country cleared: no {modeName} tariff ends in {master.CountryName(q.DestCountry)}.");
                q.DestCountry = null;
            }

            vm.SupportsPlaces = TariffRepository.PlaceTypesSupported(cols).Any();
            vm.SupportsPostcode = cols.Contains("origin_postcode") && cols.Contains("dest_postcode");
            vm.OriginPlaces = PlaceOptions(vm, q.Mode, q.Carrier, "origin", q.OriginCountry, cols);
            vm.DestPlaces = PlaceOptions(vm, q.Mode, q.Carrier, "dest", q.DestCountry, cols);
            CheckPlace(vm, "From", vm.OriginPlaces, q.OriginPlaceType, q.OriginPlace, () => { q.OriginPlaceType = null; q.OriginPlace = null; });
            CheckPlace(vm, "To", vm.DestPlaces, q.DestPlaceType, q.DestPlace, () => { q.DestPlaceType = null; q.DestPlace = null; });
            if (!vm.SupportsPostcode) q.OriginPostcode = q.DestPostcode = null;

            // Only worth saying for the countries those carriers actually serve.
            vm.UnzonedCarriers = options.Where(o => o.K == "unzoned" && (o.C == q.OriginCountry || o.C == q.DestCountry))
                                        .Select(o => o.V).Distinct().Where(TariffRepository.IsAnonymisedCode).OrderBy(c => c).ToList();

            vm.SupportsDistance = TariffRepository.HasDistance(cols);
            if (!vm.SupportsDistance && q.DistanceKm is not null)
            {
                vm.Notices.Add($"Distance cleared: the {modeName} tariff is not priced by distance.");
                q.DistanceKm = null;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or SchemaMismatchException)
        {
            vm.ConfigError = ex.Message;
        }
        return vm;
    }

    /// <summary>
    /// The places a user can pick at one end inside the chosen country: named warehouses, tariff cities,
    /// every state / province / prefecture of the country (from the master data), tariff regions that no
    /// district map covers, and tariff postcodes. Empty until a country is chosen.
    /// </summary>
    private List<PlaceOption> PlaceOptions(SearchViewModel vm, TariffMode mode, string? carrier, string side, string? country, HashSet<string> cols)
    {
        var list = new List<PlaceOption>();
        if (country is null || !vm.SupportsPlaces) return list;

        var withheld = 0;
        foreach (var r in repo.Places(mode, carrier, side, country))
        {
            switch (r.Pt)
            {
                case "CITY":
                    var locs = master.LocationsForTariffValue(r.C, r.V);
                    if (locs.Count > 0)
                        list.AddRange(locs.Select(loc => new PlaceOption("LOCATION|" + loc.Code, loc.Name, GroupLocations)));
                    else if (redactor.Hits(r.V)) withheld++;
                    else list.Add(new PlaceOption("CITY|" + r.V, r.V, GroupCities));
                    break;
                case "PROVINCE":
                    // Known subdivisions are offered from the master list below; unknown spellings as they are.
                    if (master.SubdivisionCodeOf(country, r.V) is null && !redactor.Hits(r.V))
                        list.Add(new PlaceOption("PROVINCE|" + r.V, r.V, GroupStates));
                    break;
                case "REGION":
                    // Districts of a carrier with a district map are reached through the subdivisions.
                    if (!master.HasDistrictMap(r.C) && !redactor.Hits(r.V))
                        list.Add(new PlaceOption("REGION|" + r.V, r.V + " (region)", GroupRegions));
                    break;
                case "POSTCODE":
                    if (!redactor.Hits(r.V)) list.Add(new PlaceOption("POSTCODE|" + r.V, r.V + "*", GroupPostcodes));
                    break;
            }
        }
        vm.WithheldValues += withheld;

        // States / provinces / prefectures help where a lane is priced by province or by a carrier district.
        if (cols.Contains($"{side}_state") || cols.Contains($"{side}_region"))
            foreach (var s in master.TopSubdivisions(country))
                list.Add(new PlaceOption("PROVINCE|" + s.Code, s.ShortName + (s.Type is null ? "" : $" ({s.Type.ToLowerInvariant()})"), GroupStates));

        return list.GroupBy(o => o.Value).Select(g => g.First())
                   .OrderBy(o => GroupOrder(o.Group)).ThenBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int GroupOrder(string g) => g switch
    {
        GroupLocations => 0, GroupCities => 1, GroupStates => 2, GroupRegions => 3, _ => 4
    };

    private static void CheckPlace(SearchViewModel vm, string label, List<PlaceOption> places, string? type, string? value, Action clear)
    {
        if (type is null || value is null)
        {
            clear();
            return;
        }
        if (places.All(p => p.Value != type + "|" + value))
        {
            vm.Notices.Add($"{label} place cleared: it is not in the chosen country, or no tariff of this selection uses it.");
            clear();
        }
    }

    /// <summary>Turns one end of the form into the filter the repository applies (see <see cref="PlaceFilter"/>).</summary>
    private PlaceFilter Resolve(string? country, string? type, string? value, string? postcode)
    {
        if (country is null) return PlaceFilter.Any;

        string? city = null, pattern = null, region = null;
        string[] states = [], dCarriers = [], dZones = [];
        switch (type)
        {
            case "CITY":
                city = value;
                break;
            case "LOCATION":
                pattern = value is null ? null : master.Location(value)?.Pattern;
                break;
            case "REGION":
                region = value;
                break;
            case "POSTCODE":
                postcode ??= value;
                break;
            case "PROVINCE" when value is not null:
                var code = master.SubdivisionByCode(value) is not null ? value : master.SubdivisionCodeOf(country, value);
                if (code is null)
                {
                    states = [value.Trim().ToUpperInvariant()];
                    break;
                }
                states = master.SubdivisionSpellings(code).ToArray();
                var districts = master.DistrictsOf(code);
                dCarriers = districts.Select(d => d.Carrier).ToArray();
                dZones = districts.Select(d => d.Zone).ToArray();
                break;
        }
        return new PlaceFilter(country, city, pattern, states, dCarriers, dZones, region, postcode);
    }

    /// <summary>Runs the search against an already-built form. Rows with non-anonymised carrier codes are withheld and counted.</summary>
    public void Search(SearchViewModel vm)
    {
        if (vm.ConfigError is not null) return;
        var q = vm.Query;

        try
        {
            var origin = Resolve(q.OriginCountry, q.OriginPlaceType, q.OriginPlace, q.OriginPostcode);
            var dest = Resolve(q.DestCountry, q.DestPlaceType, q.DestPlace, q.DestPostcode);
            var (rows, truncated) = repo.Search(q, origin, dest);
            vm.WithheldCarrierCodes += rows.RemoveAll(r => !TariffRepository.IsAnonymisedCode(r.CarrierCode));
            var withheld = 0;
            foreach (var r in rows)
            {
                r.OriginDesc = Describe(r.CarrierCode, r.OriginPointType, r.OriginCountryCode ?? q.OriginCountry, r.OriginPlace);
                r.DestDesc = Describe(r.CarrierCode, r.DestPointType, r.DestCountryCode ?? q.DestCountry, r.DestPlace);
                r.ServiceType = redactor.Mask(r.ServiceType, ref withheld);
                r.LaneCode = redactor.Mask(r.LaneCode, ref withheld);
                r.LaneType = redactor.Mask(r.LaneType, ref withheld);
                r.OriginDesc = redactor.Mask(r.OriginDesc, ref withheld);
                r.DestDesc = redactor.Mask(r.DestDesc, ref withheld);
                r.SurchargeCodes = redactor.Mask(r.SurchargeCodes, ref withheld);
                r.SourceRef = redactor.Mask(r.SourceRef, ref withheld);
                r.SourceSheet = redactor.Mask(r.SourceSheet, ref withheld);
            }
            MarkAddressDependent(rows);
            vm.Results = rows;
            vm.Truncated = truncated;
            vm.AdderGaps = repo.AdderGaps(q, origin, dest)
                .Where(g => TariffRepository.IsAnonymisedCode(g.CarrierCode)).ToList();
            foreach (var g in vm.AdderGaps)
            {
                g.ServiceType = redactor.Mask(g.ServiceType, ref withheld);
                g.LaneCode = redactor.Mask(g.LaneCode, ref withheld);
            }
            vm.WithheldValues += withheld;
            vm.Searched = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SchemaMismatchException)
        {
            vm.ConfigError = ex.Message;
        }
    }

    /// <summary>
    /// A carrier that splits a country by station prices one shipment in several zones (ZONE_MAP has a row per
    /// station group). Rows of the same carrier, service, lane type and band that differ only in the zone are
    /// the same offer with an address-dependent price: flag them instead of letting them look like rivals.
    /// </summary>
    private static void MarkAddressDependent(List<SearchResultRow> rows)
    {
        foreach (var g in rows.GroupBy(r => (r.CarrierCode, r.ServiceType, r.LaneType, r.WeightFromKg, r.WeightToKg,
                                             r.DistanceFromKm, r.DistanceToKm, r.Currency,
                                             O: r.OriginPointType == "ZONE" ? "ZONE" : r.OriginPlace,
                                             D: r.DestPointType == "ZONE" ? "ZONE" : r.DestPlace)))
        {
            if (g.Select(r => (r.OriginPlace, r.DestPlace)).Distinct().Count() > 1)
                foreach (var r in g) r.ZoneDependsOnAddress = true;
        }
    }

    /// <summary>Readable lane end: "Shanghai, China", "Kanto district, Japan", "Zone 3 · United Kingdom", "BE 2880*".</summary>
    private string Describe(string carrier, string? pointType, string? country, string? place)
    {
        var countryName = country is null ? null : master.CountryName(country);
        string Join(string? a, string? b) => string.Join(", ", new[] { a, b }.Where(x => !string.IsNullOrEmpty(x)));
        return pointType switch
        {
            "COUNTRY" => countryName ?? "",
            "ZONE" => countryName is null ? place ?? "" : $"{place} · {countryName}",
            "CITY" => master.LocationsForTariffValue(carrier, place) is { Count: > 0 } locs
                          ? string.Join(" / ", locs.Select(l => l.Name)) : Join(place, countryName),
            "PROVINCE" => Join(place, countryName),
            "REGION" => Join(master.HasDistrictMap(carrier) ? $"{place} district" : $"{place} region", countryName),
            "POSTCODE" => $"{country} {place}*",
            null => "—",
            _ => Join(place, countryName),
        };
    }

    /// <summary>Blank strings mean "not given".</summary>
    private static void Normalise(SearchQuery q)
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        q.Carrier = N(q.Carrier);
        foreach (var def in TariffRepository.Filters) q.SetFilter(def.Key, N(q.GetFilter(def.Key)));
        q.OriginCountry = N(q.OriginCountry);
        q.DestCountry = N(q.DestCountry);
        q.OriginPlaceType = N(q.OriginPlaceType);
        q.DestPlaceType = N(q.DestPlaceType);
        q.OriginPlace = N(q.OriginPlace);
        q.DestPlace = N(q.DestPlace);
        q.OriginPostcode = N(q.OriginPostcode);
        q.DestPostcode = N(q.DestPostcode);
        if (q.OriginCountry is null) q.OriginPostcode = null;
        if (q.DestCountry is null) q.DestPostcode = null;
        q.ShipDate ??= DateTime.Today;
    }
}
