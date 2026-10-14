using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using TariffHub.Data;
using TariffHub.Models;

namespace TariffHub.Logic;

/// <summary>
/// The ocean search, kept apart from the ground / air pipeline because ocean is priced differently:
/// FCL per container (equipment type), LCL by weight / measure (W/M, 1 cbm = 1000 kg, with a min and a max).
/// The form drills country -> port (-> city when a port serves several). A port chosen without a city quotes
/// every city-lane under that port pair, since prices can differ by city.
/// </summary>
public sealed class OceanService(TariffRepository repo, Redactor redactor, MasterData master)
{
    private const int MaxLanes = 50;

    public OceanViewModel BuildForm(OceanQuery q)
    {
        Normalise(q);
        var vm = new OceanViewModel { Query = q };
        if (repo.ConfigurationError is { } configError) { vm.ConfigError = configError; return vm; }

        try
        {
            vm.Installed = repo.OceanInstalled();
            if (!vm.Installed) return vm;

            var carriers = repo.OceanCarriers();
            vm.WithheldCarrierCodes = carriers.Count(c => !TariffRepository.IsAnonymisedCode(c));
            vm.Carriers = carriers.Where(TariffRepository.IsAnonymisedCode).ToList();
            if (q.Carrier is not null && !vm.Carriers.Contains(q.Carrier)) { vm.Notices.Add($"Carrier cleared: {q.Carrier} has no ocean tariff."); q.Carrier = null; }

            vm.Equipment = repo.OceanEquipment();
            if (q.IsFcl && q.Equipment is not null && !vm.Equipment.Contains(q.Equipment))
            { vm.Notices.Add($"Equipment cleared: {q.Equipment} is not priced."); q.Equipment = null; }
            if (!q.IsFcl) { q.Equipment = null; q.Containers = null; }

            vm.OriginCountries = Countries(repo.OceanCountries(true, q.Service, q.Carrier));
            vm.DestCountries = Countries(repo.OceanCountries(false, q.Service, q.Carrier));
            if (q.OriginCountry is not null && vm.OriginCountries.All(o => o.Value != q.OriginCountry))
            { vm.Notices.Add("From country cleared: no ocean lane starts there for this selection."); q.OriginCountry = q.OriginPort = q.OriginCity = null; }
            if (q.DestCountry is not null && vm.DestCountries.All(o => o.Value != q.DestCountry))
            { vm.Notices.Add("To country cleared: no ocean lane ends there for this selection."); q.DestCountry = q.DestPort = q.DestCity = null; }

            // Ports depend on the country; cities depend on the port. Clear what no longer fits as the user drills.
            if (q.OriginCountry is not null)
            {
                vm.OriginPorts = repo.OceanPorts(true, q.Service, q.Carrier, q.OriginCountry);
                if (q.OriginPort is not null && vm.OriginPorts.All(p => p.Code != q.OriginPort)) { q.OriginPort = q.OriginCity = null; }
                if (q.OriginPort is not null)
                {
                    vm.OriginCities = repo.OceanCities(true, q.Service, q.Carrier, q.OriginCountry, q.OriginPort);
                    if (q.OriginCity is not null && !vm.OriginCities.Contains(q.OriginCity)) q.OriginCity = null;
                }
            }
            if (q.DestCountry is not null)
            {
                vm.DestPorts = repo.OceanPorts(false, q.Service, q.Carrier, q.DestCountry);
                if (q.DestPort is not null && vm.DestPorts.All(p => p.Code != q.DestPort)) { q.DestPort = q.DestCity = null; }
                if (q.DestPort is not null)
                {
                    vm.DestCities = repo.OceanCities(false, q.Service, q.Carrier, q.DestCountry, q.DestPort);
                    if (q.DestCity is not null && !vm.DestCities.Contains(q.DestCity)) q.DestCity = null;
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or SchemaMismatchException)
        {
            vm.ConfigError = ex.Message;
        }
        return vm;
    }

    private List<Option> Countries(IEnumerable<string> codes) => codes
        .Select(c => new Option(c, master.CountryLabel(c)))
        .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList();

    public void Search(OceanViewModel vm)
    {
        if (vm.ConfigError is not null || !vm.Installed) return;
        var q = vm.Query;
        try
        {
            // Need at least one port on each end so the result stays a handful of lanes, not the whole ocean table.
            if (q.OriginPort is null || q.DestPort is null)
            {
                vm.Notices.Add("Choose an origin port and a destination port (country, then port) to see a quote.");
                vm.Searched = true;
                return;
            }

            var rows = repo.OceanCharges(q, MaxLanes);
            var ship = q.ShipDate ?? DateTime.Today;
            var containers = q.Containers is { } n && n > 0 ? n : 1;
            decimal? wm = q.IsFcl ? null : WmQuantity(q);

            var carrier = repo.OceanCarrierOf(q);
            var withheld = 0;
            foreach (var lane in rows.GroupBy(r => (r.OriginCity, r.OriginPort, r.DestCity, r.DestPort)))
            {
                var res = new OceanQuoteResult
                {
                    Currency = carrier?.Currency ?? "USD",
                    CarrierCode = carrier?.Carrier ?? "",
                    Service = q.Service,
                    Routing = q.Routing,
                    Equipment = q.IsFcl ? q.Equipment : "LCL",
                    OriginDesc = LaneEnd(q.OriginCountry, lane.Key.OriginCity, lane.Key.OriginPort),
                    DestDesc = LaneEnd(q.DestCountry, lane.Key.DestCity, lane.Key.DestPort),
                    Basis = q.IsFcl
                        ? $"{containers} × {q.Equipment}"
                        : wm is { } v ? $"{v:0.##} W/M" : "W/M (enter weight or volume)",
                };

                foreach (var r in lane)
                {
                    var line = new OceanLine
                    {
                        Leg = r.Leg, ChargeCode = r.ChargeCode, ChargeName = r.ChargeName,
                        ChargeBasis = r.ChargeBasis, Rate = r.RateValue, MinCharge = r.MinCharge, MaxCharge = r.MaxCharge,
                    };
                    Price(line, containers, wm);
                    res.Lines.Add(line);
                }

                // FCL takes the month's Ocean Rate Increase on top of the ocean freight.
                if (q.IsFcl && q.Equipment is not null
                    && repo.OceanOri(lane.Key.OriginPort, lane.Key.DestPort, q.Equipment, ship) is { } adder)
                {
                    res.Ori = adder;
                    res.OriNote = $"Ocean rate increase for {ship:yyyy-MM}";
                    res.Lines.Add(new OceanLine
                    {
                        Leg = "OCEAN", ChargeCode = "ORI", ChargeName = "Ocean rate increase",
                        ChargeBasis = "PER_CONTAINER", Rate = adder, Amount = adder * containers, Note = res.OriNote,
                    });
                }

                res.PortToPort = res.Lines.Where(l => l.Leg == "OCEAN" && l.Amount is not null).Sum(l => l.Amount!.Value);
                res.DoorToDoor = res.Lines.Where(l => l.Amount is not null).Sum(l => l.Amount!.Value);
                if (res.Lines.Any(l => l.Amount is null))
                    res.Notes.Add("Some charges are not priced on this page (shown with a reason); they are not in the total.");

                // Redaction: a non-anonymised carrier code or a charge name carrying a real party name is withheld.
                if (!TariffRepository.IsAnonymisedCode(res.CarrierCode)) { vm.WithheldCarrierCodes++; continue; }
                foreach (var l in res.Lines) l.ChargeName = redactor.Mask(l.ChargeName, ref withheld);
                res.OriginDesc = redactor.Mask(res.OriginDesc, ref withheld) ?? "";
                res.DestDesc = redactor.Mask(res.DestDesc, ref withheld) ?? "";
                vm.Results.Add(res);
            }

            if (q.IsFcl && q.Equipment is null) vm.Notices.Add("Choose an equipment type (container size) for an FCL quote.");
            if (!q.IsFcl && wm is null) vm.Notices.Add("Enter the weight and/or volume: LCL is priced by weight or measure, whichever is greater.");
            if (rows.Select(r => (r.OriginCity, r.OriginPort, r.DestCity, r.DestPort)).Distinct().Count() >= MaxLanes)
                vm.Notices.Add($"Only the first {MaxLanes} city-lanes are shown; pick a city on each end to narrow.");
            vm.WithheldValues += withheld;
            // Cheapest door-to-door first.
            vm.Results = vm.Results.OrderBy(r => r.DoorToDoor).ToList();
            vm.Searched = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SchemaMismatchException)
        {
            vm.ConfigError = ex.Message;
        }
    }

    /// <summary>LCL chargeable quantity: the greater of the volume (cbm) and the weight converted at 1 cbm = 1000 kg.</summary>
    private static decimal? WmQuantity(OceanQuery q)
    {
        decimal? byVol = q.VolumeCbm;
        decimal? byWt = q.WeightKg is { } kg ? kg / 1000m : null;
        if (byVol is null && byWt is null) return null;
        return Math.Max(byVol ?? 0m, byWt ?? 0m);
    }

    /// <summary>One line priced by its basis, then held between the min and the max of the card.</summary>
    private static void Price(OceanLine line, int containers, decimal? wm)
    {
        decimal? raw = line.ChargeBasis switch
        {
            "PER_CONTAINER" => line.Rate is { } r ? r * containers : null,
            "PER_SHIPMENT" => line.Rate,
            "PER_WM" => wm is { } q && line.Rate is { } r ? q * r : null,
            _ => null,
        };
        if (raw is null)
        {
            line.Note = line.ChargeBasis == "PER_WM" ? "enter weight or volume" : "not priced on this page";
            return;
        }
        var amount = raw.Value;
        if (line.MinCharge is { } min && amount < min) { amount = min; line.LimitApplied = "min"; }
        if (line.MaxCharge is { } max && amount > max) { amount = max; line.LimitApplied = "max"; }
        line.Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>"Ansan, Korea (PUS)".</summary>
    private string LaneEnd(string? country, string? city, string port)
    {
        var name = country is null ? null : master.CountryName(country);
        var place = string.Join(", ", new[] { city, name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return place.Length == 0 ? port : $"{place} ({port})";
    }

    private static void Normalise(OceanQuery q)
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        q.Service = string.Equals(q.Service, "LCL", StringComparison.OrdinalIgnoreCase) ? "LCL" : "FCL";
        q.Routing = string.Equals(q.Routing, "BACKUP", StringComparison.OrdinalIgnoreCase) ? "BACKUP" : "PRIMARY";
        q.Carrier = N(q.Carrier);
        q.OriginCountry = N(q.OriginCountry); q.OriginPort = N(q.OriginPort); q.OriginCity = N(q.OriginCity);
        q.DestCountry = N(q.DestCountry); q.DestPort = N(q.DestPort); q.DestCity = N(q.DestCity);
        q.Equipment = N(q.Equipment);
        if (q.OriginCountry is null) { q.OriginPort = q.OriginCity = null; }
        if (q.OriginPort is null) q.OriginCity = null;
        if (q.DestCountry is null) { q.DestPort = q.DestCity = null; }
        if (q.DestPort is null) q.DestCity = null;
        q.ShipDate ??= DateTime.Today;
    }
}
