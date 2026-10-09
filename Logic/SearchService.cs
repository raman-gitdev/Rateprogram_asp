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
    private const string GroupAirports = "Airports";

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
            // Ground tables carry postcodes on the lanes; air zone charts can split a country by postcode.
            vm.SupportsPostcode = (cols.Contains("origin_postcode") && cols.Contains("dest_postcode"))
                                  || (q.Mode == TariffMode.Air && repo.AirZonePostcodes());
            vm.SupportsAirports = cols.Contains("origin_airport") && cols.Contains("dest_airport");
            vm.OriginAirports = AirportOptions(vm, q.Mode, q.Carrier, "origin", q.OriginCountry);
            vm.DestAirports = AirportOptions(vm, q.Mode, q.Carrier, "dest", q.DestCountry);
            if (q.OriginAirport is not null && vm.OriginAirports.All(o => o.Value != q.OriginAirport))
            {
                if (q.OriginCountry is not null) vm.Notices.Add("From airport cleared: no tariff of this selection uses it in the chosen country.");
                q.OriginAirport = null;
            }
            if (q.DestAirport is not null && vm.DestAirports.All(o => o.Value != q.DestAirport))
            {
                if (q.DestCountry is not null) vm.Notices.Add("To airport cleared: no tariff of this selection uses it in the chosen country.");
                q.DestAirport = null;
            }
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
                // Airports have their own dropdown (AirportOptions).
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

    /// <summary>
    /// The airports a user can pick at one end inside the chosen country, labelled with the full name from the
    /// airport master ("DLC - Zhoushuizi Airport, Dalian"). Empty until a country is chosen or when the table has
    /// no airport lanes.
    /// </summary>
    private List<Option> AirportOptions(SearchViewModel vm, TariffMode mode, string? carrier, string side, string? country)
    {
        if (country is null || !vm.SupportsAirports) return new();
        var names = repo.Airports();
        return repo.Places(mode, carrier, side, country)
                   .Where(r => r.Pt == "AIRPORT" && !redactor.Hits(r.V))
                   .Select(r => r.V.Trim().ToUpperInvariant()).Distinct()
                   .Select(code => new Option(code, AirportLabel(code, names)))
                   .OrderBy(o => o.Value, StringComparer.Ordinal).ToList();
    }

    private static string AirportLabel(string code, Dictionary<string, AirportInfo> names) =>
        names.TryGetValue(code, out var a)
            ? $"{code} - {a.Name}" + (string.IsNullOrWhiteSpace(a.City) || a.Name.IndexOf(a.City, StringComparison.OrdinalIgnoreCase) >= 0 ? "" : $", {a.City}")
            : code;

    private static int GroupOrder(string g) => g switch
    {
        GroupAirports => 0, GroupLocations => 1, GroupCities => 2, GroupStates => 3, GroupRegions => 4, _ => 5
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
    private PlaceFilter Resolve(string? country, string? type, string? value, string? postcode, string? airportCode)
    {
        if (country is null) return PlaceFilter.Any;

        string? city = null, pattern = null, region = null, airport = airportCode;
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
            case "AIRPORT":
                airport = value;
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
        return new PlaceFilter(country, city, pattern, states, dCarriers, dZones, region, postcode, airport);
    }

    /// <summary>Runs the search against an already-built form. Rows with non-anonymised carrier codes are withheld and counted.</summary>
    public void Search(SearchViewModel vm)
    {
        if (vm.ConfigError is not null) return;
        var q = vm.Query;

        try
        {
            var origin = Resolve(q.OriginCountry, q.OriginPlaceType, q.OriginPlace, q.OriginPostcode, q.OriginAirport);
            var dest = Resolve(q.DestCountry, q.DestPlaceType, q.DestPlace, q.DestPostcode, q.DestAirport);
            var (rows, truncated) = repo.Search(q, origin, dest);
            vm.WithheldCarrierCodes += rows.RemoveAll(r => !TariffRepository.IsAnonymisedCode(r.CarrierCode));
            // Before masking: the lane key must use the stored values.
            AttachAccessorials(q, rows);
            var withheld = 0;
            foreach (var r in rows)
            {
                r.OriginDesc = Describe(r.CarrierCode, r.OriginPointType, r.OriginCountryCode ?? q.OriginCountry, r.OriginPlace, r.OriginRegion);
                r.DestDesc = Describe(r.CarrierCode, r.DestPointType, r.DestCountryCode ?? q.DestCountry, r.DestPlace, r.DestRegion);
                r.OriginTitle = AirportTitle(r.OriginPointType, r.OriginPlace);
                r.DestTitle = AirportTitle(r.DestPointType, r.DestPlace);
                r.ServiceType = redactor.Mask(r.ServiceType, ref withheld);
                r.ServiceName = redactor.Mask(r.ServiceName, ref withheld);
                foreach (var a in r.Accessorials)
                    a.ChargeName = redactor.Mask(a.ChargeName, ref withheld) ?? "";
                r.LaneCode = redactor.Mask(r.LaneCode, ref withheld);
                r.LaneType = redactor.Mask(r.LaneType, ref withheld);
                r.OriginDesc = redactor.Mask(r.OriginDesc, ref withheld);
                r.DestDesc = redactor.Mask(r.DestDesc, ref withheld);
                r.SurchargeCodes = redactor.Mask(r.SurchargeCodes, ref withheld);
                r.SourceRef = redactor.Mask(r.SourceRef, ref withheld);
                r.SourceSheet = redactor.Mask(r.SourceSheet, ref withheld);
            }
            MarkAddressDependent(rows);
            if (vm.SupportsPostcode && rows.Any(r => r.ZoneDependsOnAddress)
                && string.IsNullOrWhiteSpace(q.OriginPostcode) && string.IsNullOrWhiteSpace(q.DestPostcode))
                vm.Notices.Add("Some carriers price this country by postcode area: enter the postcode for the exact zone and price.");
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
    /// Gives every row the accessorial charges of its carrier, service and lane (all lane columns equal, empty = empty),
    /// priced for this shipment. They are listed, not added: the user picks which apply.
    /// </summary>
    private void AttachAccessorials(SearchQuery q, List<SearchResultRow> rows)
    {
        if (rows.Count == 0) return;
        var all = repo.Accessorials(q.Mode, rows.Select(r => r.CarrierCode),
                                    rows.Where(r => r.ServiceType is not null).Select(r => r.ServiceType!),
                                    q.ShipDate ?? DateTime.Today);
        if (all.Count == 0) return;

        var rules = repo.ServiceRules();
        // Point type ANY on both sides = a carrier-wide charge (express accessorial list): it goes on every row of
        // the carrier, limited by movement.
        static bool IsCarrierWide(AccessorialRow a) => a.OriginPointType == "ANY" && a.DestPointType == "ANY";
        var carrierWide = all.Where(IsCarrierWide).ToLookup(a => a.CarrierCode.Trim().ToUpperInvariant());
        var byLane = all.Where(a => !IsCarrierWide(a)).ToLookup(a => LaneKey(a.CarrierCode,
            a.OriginPointType, a.OriginCountryCode, a.OriginRegion, a.OriginCity, a.OriginAirport,
            a.DestPointType, a.DestCountryCode, a.DestRegion, a.DestCity, a.DestAirport));
        foreach (var r in rows)
        {
            var key = LaneKey(r.CarrierCode,
                r.OriginPointType, r.OriginCountryCode, r.OriginRegion, r.OriginCity, r.OriginAirport,
                r.DestPointType, r.DestCountryCode, r.DestRegion, r.DestCity, r.DestAirport);
            r.Accessorials = byLane[key].Concat(carrierWide[r.CarrierCode.Trim().ToUpperInvariant()]
                                                    .Where(a => MovementMatches(a.AppliesToMovement, r.LaneType)))
                                        .Where(a => Applies(a, r.ServiceType, rules))
                                        .Select(a => PriceAccessorial(a, r, q)).ToList();
        }
    }

    /// <summary>
    /// A carrier-wide charge (movement DOMESTIC / INTERNATIONAL / EXPORT / IMPORT) against the row's lane type:
    /// EXPORT = OUTBOUND, IMPORT = INBOUND, INTERNATIONAL = either. No movement, or no lane type, matches all.
    /// </summary>
    private static bool MovementMatches(string? movement, string? laneType)
    {
        if (string.IsNullOrWhiteSpace(movement) || string.IsNullOrWhiteSpace(laneType)) return true;
        var lane = laneType!.Trim().ToUpperInvariant();
        return movement!.Trim().ToUpperInvariant() switch
        {
            "DOMESTIC" => lane == "DOMESTIC",
            "EXPORT" => lane is "OUTBOUND" or "EXPORT",
            "IMPORT" => lane is "INBOUND" or "IMPORT",
            "INTERNATIONAL" => lane is "OUTBOUND" or "INBOUND" or "EXPORT" or "IMPORT" or "CROSS_TRADE",
            _ => true,
        };
    }

    /// <summary>
    /// A charge naming a service applies to that service only. A charge without one is a lane charge: it applies to
    /// every service whose tariff.service_type row includes its side (DTD both, DTA origin, ATD destination, ATA none).
    /// A service missing from the master gets every lane charge.
    /// </summary>
    private static bool Applies(AccessorialRow a, string? service, Dictionary<string, (bool Origin, bool Destination)> rules)
    {
        if (a.ServiceType is not null) return string.Equals(a.ServiceType, service, StringComparison.OrdinalIgnoreCase);
        if (service is null || !rules.TryGetValue(service, out var rule)) return true;
        return a.ChargeSide switch
        {
            "ORIGIN" => rule.Origin,
            "DESTINATION" => rule.Destination,
            _ => true,
        };
    }

    private static string LaneKey(params string?[] parts) =>
        string.Join("|", parts.Select(x => x?.Trim().ToUpperInvariant() ?? ""));

    /// <summary>
    /// One charge for one row: rate by its basis, then held between min and max. Null amount (with a reason)
    /// when an input is missing or the currency differs from the freight - never zero.
    /// </summary>
    private static AccessorialCharge PriceAccessorial(AccessorialRow a, SearchResultRow r, SearchQuery q)
    {
        var c = new AccessorialCharge
        {
            ChargeCode = a.ChargeCode, ChargeName = a.ChargeName, ChargeSide = a.ChargeSide, ChargeBasis = a.ChargeBasis,
            Rate = a.RateValue, MinCharge = a.MinCharge, MaxCharge = a.MaxCharge, Currency = a.CurrencyCode,
        };
        if (a.ChargeBasis != "PCT_OF_FREIGHT" && !string.Equals(a.CurrencyCode, r.Currency, StringComparison.OrdinalIgnoreCase))
        {
            c.NotPricedReason = $"quoted in {a.CurrencyCode}, freight in {r.Currency}";
            return c;
        }

        decimal? raw = a.ChargeBasis switch
        {
            "PER_KG" => r.ChargeableKg is { } kg ? a.RateValue * kg : (decimal?)null,
            "PER_SHIPMENT" or "PER_DECLARATION" or "PER_ENTRY" or "PER_AWB" => (decimal?)a.RateValue,
            "PCT_OF_FREIGHT" => r.BaseFreight is { } f ? a.RateValue / 100m * f : (decimal?)null,
            "PER_PIECE" => q.Pieces is { } n ? a.RateValue * n : (decimal?)null,
            _ => null,
        };
        if (raw is null)
        {
            c.NotPricedReason = a.ChargeBasis switch
            {
                "PER_KG" => "enter weight or volume",
                "PCT_OF_FREIGHT" => "freight not priced",
                "PER_PIECE" => "enter pieces",
                _ => "not priced on this page",
            };
            return c;
        }

        var amount = raw.Value;
        if (a.MinCharge is { } min && amount < min) { amount = min; c.LimitApplied = "min"; }
        if (a.MaxCharge is { } max && amount > max) { amount = max; c.LimitApplied = "max"; }
        c.Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return c;
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
            // The region counts too: China local zone 1 and 2 are the same lane end priced in two zones.
            if (g.Select(r => (r.OriginPlace, r.DestPlace, r.OriginRegion, r.DestRegion)).Distinct().Count() > 1)
                foreach (var r in g) r.ZoneDependsOnAddress = true;
        }
    }

    private string? AirportCity(string? code) =>
        code is not null && repo.Airports().TryGetValue(code.Trim(), out var a) ? a.City : null;

    /// <summary>Tooltip of an airport lane end: "DLC - Zhoushuizi Airport, Dalian".</summary>
    private string? AirportTitle(string? pointType, string? code) =>
        pointType == "AIRPORT" && code is not null ? AirportLabel(code.Trim().ToUpperInvariant(), repo.Airports()) : null;

    /// <summary>Readable lane end: "Shanghai, China", "Kanto district, Japan", "Zone 3 · United Kingdom", "BE 2880*".</summary>
    private string Describe(string carrier, string? pointType, string? country, string? place, string? region = null)
    {
        var countryName = country is null ? null : master.CountryName(country);
        string Join(string? a, string? b) => string.Join(", ", new[] { a, b }.Where(x => !string.IsNullOrEmpty(x)));
        return pointType switch
        {
            // A country lane that still carries a zone (e.g. a local delivery zone) shows it: it is what tells the rows apart.
            "COUNTRY" => string.IsNullOrWhiteSpace(region) ? countryName ?? "" : $"{countryName} · {region}",
            // "DLC · Dalian, China": the code, then where it is; the full airport name is the cell's tooltip.
            "AIRPORT" => $"{place?.Trim()} · " + Join(AirportCity(place), countryName),
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
