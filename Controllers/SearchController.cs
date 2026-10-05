using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TariffHub.Data;
using TariffHub.Models;

namespace TariffHub.Controllers;

public class SearchController(TariffRepository repo) : Controller
{
    /// <summary>Empty form, or the form re-rendered after a mode/carrier change (lists reload, stale filters clear).</summary>
    [HttpGet]
    public async Task<IActionResult> Index(SearchQuery query)
    {
        var vm = await BuildFormAsync(query);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SearchQuery query, bool search = true)
    {
        var vm = await BuildFormAsync(query);
        if (vm.ConfigError is not null || !search) return View(vm);

        try
        {
            var (rows, truncated) = await repo.SearchAsync(vm.Query);
            vm.WithheldCarrierCodes += rows.RemoveAll(r => !TariffRepository.IsAnonymisedCode(r.CarrierCode));
            vm.Results = rows;
            vm.Truncated = truncated;
            vm.AdderGaps = (await repo.AdderGapsAsync(vm.Query))
                .Where(g => TariffRepository.IsAnonymisedCode(g.CarrierCode)).ToList();
            vm.Searched = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SchemaMismatchException)
        {
            vm.ConfigError = ex.Message;
        }
        return View(vm);
    }

    private async Task<SearchViewModel> BuildFormAsync(SearchQuery q)
    {
        Normalise(q);
        var vm = new SearchViewModel { Query = q };

        if (repo.ConfigurationError is { } configError)
        {
            vm.ConfigError = configError;
            return vm;
        }

        try
        {
            var cols = await repo.ColumnsAsync(q.Mode);
            var modeName = q.Mode.ToString().ToLowerInvariant();

            // Carrier first: every other list is scoped to it.
            var all = await repo.FilterOptionsAsync(q.Mode, null);
            var carriers = all.Where(o => o.K == "carrier").Select(o => o.V).Distinct().ToList();
            vm.WithheldCarrierCodes = carriers.Count(c => !TariffRepository.IsAnonymisedCode(c));
            vm.Carriers = carriers.Where(TariffRepository.IsAnonymisedCode).ToList();
            if (q.Carrier is not null && !vm.Carriers.Contains(q.Carrier))
            {
                vm.Notices.Add($"Carrier cleared: {q.Carrier} has no {modeName} tariff.");
                q.Carrier = null;
            }

            var options = q.Carrier is null ? all : await repo.FilterOptionsAsync(q.Mode, q.Carrier);
            List<string> Values(string key) => options.Where(o => o.K == key).Select(o => o.V).Distinct().ToList();
            var carrierScope = q.Carrier is null ? $"No {modeName} tariff" : $"Carrier {q.Carrier}";

            foreach (var def in TariffRepository.Filters)
            {
                var selected = q.GetFilter(def.Key);
                FilterField field;
                if (TariffRepository.FilterColumn(def, cols) is null)
                {
                    field = new FilterField { Key = def.Key, Label = def.Label, DisabledNote = $"Not part of the {modeName} tariff." };
                }
                else
                {
                    var values = Values(def.Key);
                    var users = options.Where(o => o.K == def.Key).Select(o => o.C).Distinct()
                                       .Where(TariffRepository.IsAnonymisedCode).OrderBy(c => c).ToList();
                    field = new FilterField
                    {
                        Key = def.Key, Label = def.Label, Options = values, Carriers = users,
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

            vm.OriginCountries = Values("ocountry");
            vm.DestCountries = Values("dcountry");
            if (q.OriginCountry is not null && !vm.OriginCountries.Contains(q.OriginCountry))
            {
                vm.Notices.Add($"Origin country cleared: no {modeName} tariff starts in {q.OriginCountry}.");
                q.OriginCountry = null;
            }
            if (q.DestCountry is not null && !vm.DestCountries.Contains(q.DestCountry))
            {
                vm.Notices.Add($"Destination country cleared: no {modeName} tariff ends in {q.DestCountry}.");
                q.DestCountry = null;
            }

            var pointTypes = Values("pt");
            vm.PlaceTypes = TariffRepository.PlaceTypesSupported(cols).Where(pointTypes.Contains).ToList();
            vm.SupportsPlaces = vm.PlaceTypes.Count > 0;
            ClearPlace(vm, "Origin", q.OriginPlaceType, q.OriginPlace, () => { q.OriginPlaceType = null; q.OriginPlace = null; });
            ClearPlace(vm, "Destination", q.DestPlaceType, q.DestPlace, () => { q.DestPlaceType = null; q.DestPlace = null; });

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

    private static void ClearPlace(SearchViewModel vm, string label, string? type, string? place, Action clear)
    {
        if (place is null && type is null) return;
        if (place is null || type is null)
        {
            if (place is not null) vm.Notices.Add($"{label} place ignored: choose what kind of place it is.");
            clear();
            return;
        }
        if (!vm.PlaceTypes.Contains(type))
        {
            vm.Notices.Add($"{label} place cleared: no lane in this tariff is addressed by {type}.");
            clear();
        }
    }

    /// <summary>Blank strings bind as empty; treat them as "not given".</summary>
    private static void Normalise(SearchQuery q)
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        q.Carrier = N(q.Carrier);
        foreach (var def in TariffRepository.Filters) q.SetFilter(def.Key, N(q.GetFilter(def.Key)));
        q.OriginCountry = N(q.OriginCountry);
        q.DestCountry = N(q.DestCountry);
        q.OriginPlaceType = N(q.OriginPlaceType);
        q.DestPlaceType = N(q.DestPlaceType);
        q.OriginPlace = N(q.OriginPlace);
        q.DestPlace = N(q.DestPlace);
        q.ShipDate ??= DateTime.Today;
    }
}
