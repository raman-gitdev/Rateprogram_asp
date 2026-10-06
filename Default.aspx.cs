using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using TariffHub.Data;
using TariffHub.Logic;
using TariffHub.Models;

namespace TariffHub
{
    /// <summary>
    /// Rate search. View state is off: every request reads the posted form into a <see cref="SearchQuery"/>,
    /// rebuilds every list from the database, and re-renders. A mode or carrier change posts back without searching.
    /// </summary>
    public partial class _Default : Page
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private SearchViewModel _vm = new();

        protected void Page_Load(object sender, EventArgs e)
        {
            var query = IsPostBack ? ReadQuery() : new SearchQuery();
            var service = new SearchService(Global.Repository, Global.Redactor, Global.Master);
            var vm = service.BuildForm(query);

            // Only the Search button posts its own name; AutoPostBack from mode, carrier and country does not.
            if (IsPostBack && Request.Form[btnSearch.UniqueID] is not null)
                service.Search(vm);

            Bind(vm);
        }

        // ------------------------------------------------------------ read form

        private SearchQuery ReadQuery()
        {
            string? F(Control c) => Request.Form[c.UniqueID];

            var q = new SearchQuery
            {
                Mode = Enum.TryParse<TariffMode>(F(ddlMode), out var mode) ? mode : TariffMode.Ground,
                ShipDate = DateTime.TryParseExact(F(txtShipDate), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null,
                Carrier = F(ddlCarrier),
                OriginCountry = F(ddlOriginCountry),
                OriginPostcode = F(txtOriginPostcode),
                DestCountry = F(ddlDestCountry),
                DestPostcode = F(txtDestPostcode),
                WeightKg = Dec(F(txtWeight)),
                VolumeCbm = Dec(F(txtVolume)),
                LengthCm = Dec(F(txtLength)),
                WidthCm = Dec(F(txtWidth)),
                HeightCm = Dec(F(txtHeight)),
                Pieces = int.TryParse(F(txtPieces), NumberStyles.Integer, Inv, out var n) ? n : null,
                DistanceKm = Dec(F(txtDistance)),
            };
            foreach (var def in TariffRepository.Filters)
                q.SetFilter(def.Key, Request.Form["f_" + def.Key]);
            (q.OriginPlaceType, q.OriginPlace) = SplitPlace(Request.Form["origin_place"]);
            (q.DestPlaceType, q.DestPlace) = SplitPlace(Request.Form["dest_place"]);
            return q;
        }

        /// <summary>"PROVINCE|JP-13" -> (PROVINCE, JP-13). Anything else -> no place.</summary>
        private static (string? Type, string? Value) SplitPlace(string? v)
        {
            var i = v?.IndexOf('|') ?? -1;
            return i > 0 ? (v!.Substring(0, i), v.Substring(i + 1)) : (null, null);
        }

        private static decimal? Dec(string? s) =>
            decimal.TryParse(s, NumberStyles.Number, Inv, out var v) && v >= 0 ? v : null;

        // ----------------------------------------------------------- bind form

        private void Bind(SearchViewModel vm)
        {
            _vm = vm;
            var q = vm.Query;

            ddlMode.SelectedValue = q.Mode.ToString();
            txtShipDate.Text = q.ShipDate?.ToString("yyyy-MM-dd", Inv) ?? "";
            Fill(ddlCarrier, vm.Carriers, "All carriers", q.Carrier);

            Fill(ddlOriginCountry, vm.OriginCountries, "Any country", q.OriginCountry);
            Fill(ddlDestCountry, vm.DestCountries, "Any country", q.DestCountry);
            phOriginPlace.Visible = phDestPlace.Visible = vm.SupportsPlaces;
            phOriginNoPlace.Visible = phDestNoPlace.Visible = !vm.SupportsPlaces;
            phOriginPostcode.Visible = phDestPostcode.Visible = vm.SupportsPostcode;
            txtOriginPostcode.Text = q.OriginPostcode ?? "";
            txtDestPostcode.Text = q.DestPostcode ?? "";
            txtOriginPostcode.Enabled = q.OriginCountry is not null;
            txtDestPostcode.Enabled = q.DestCountry is not null;

            txtWeight.Text = Num(q.WeightKg);
            txtVolume.Text = Num(q.VolumeCbm);
            txtLength.Text = Num(q.LengthCm);
            txtWidth.Text = Num(q.WidthCm);
            txtHeight.Text = Num(q.HeightCm);
            txtPieces.Text = q.Pieces?.ToString(Inv) ?? "";
            txtDistance.Text = Num(q.DistanceKm);
            txtDistance.Enabled = vm.SupportsDistance;
            litDistanceNote.Text = vm.SupportsDistance ? "" : "<p class=\"small muted\">Not priced by distance.</p>";
            // Hidden when none of its fields exist in this mode (air has no trucks, classes or distance).
            phVehicle.Visible = vm.SupportsDistance
                || vm.Filters.Any(f => (f.Key == "equipment" || f.Key == "oversize") && !f.NotInMode);

            litMessages.Text = Messages(vm);

            rptGaps.DataSource = vm.AdderGaps;
            rptGaps.DataBind();

            phResults.Visible = vm.Searched && vm.Results.Count > 0;
            litResultHeading.Text = vm.Searched ? ResultHeading(vm) : "";
            if (phResults.Visible)
            {
                rptResults.DataSource = vm.Results;
                rptResults.DataBind();
            }
        }

        private static void Fill(DropDownList ddl, IEnumerable<string> values, string emptyText, string? selected) =>
            Fill(ddl, values.Select(v => new Option(v, v)), emptyText, selected);

        private static void Fill(DropDownList ddl, IEnumerable<Option> options, string emptyText, string? selected)
        {
            ddl.Items.Clear();
            ddl.Items.Add(new ListItem(emptyText, ""));
            foreach (var o in options) ddl.Items.Add(new ListItem(o.Label, o.Value));
            ddl.SelectedValue = selected ?? "";
        }

        private static string Messages(SearchViewModel vm)
        {
            var sb = new StringBuilder();
            void Alert(string cls, string text) =>
                sb.Append("<div class=\"").Append(cls).Append("\">").Append(HttpUtility.HtmlEncode(text)).Append("</div>");

            if (vm.ConfigError is not null) Alert("err", vm.ConfigError);
            foreach (var n in vm.Notices) Alert("notice", n);
            if (vm.UnzonedCarriers.Count > 0 && (vm.Query.OriginCountry is not null || vm.Query.DestCountry is not null))
                Alert("info",
                    $"{string.Join(", ", vm.UnzonedCarriers)} price{(vm.UnzonedCarriers.Count == 1 ? "s" : "")} by carrier zone but no zone chart is loaded, " +
                    "so they cannot be matched to a country or place and are left out. Clear both countries to list their zones.");
            if (vm.WithheldCarrierCodes > 0)
                Alert("err",
                    $"{vm.WithheldCarrierCodes} carrier code(s) do not follow the anonymised pattern and are withheld from display. Report this to the data owner.");
            if (vm.WithheldValues > 0)
                Alert("err",
                    $"{vm.WithheldValues} value(s) on this page contain a real party name and are shown as {Redactor.Placeholder} or left out of the lists. Report this to the data owner.");
            return sb.ToString();
        }

        // ------------------------------------------------------ markup helpers

        private static string ResultHeading(SearchViewModel vm)
        {
            var n = vm.Results.Count;
            var sb = new StringBuilder("<h3>").Append(n).Append(n == 1 ? " rate" : " rates");
            if (vm.Truncated)
                sb.Append(" <span class=\"warn\">· first ").Append(TariffRepository.MaxResults).Append(" shown, narrow the filters</span>");
            sb.Append("</h3>");
            if (n == 0 && vm.AdderGaps.Count == 0)
                sb.Append("<p class=\"muted\">No tariff matches these filters. Check the kind of place (city, postcode, region or province) and the ship date.</p>");
            return sb.ToString();
        }

        /// <summary>One filter as a labelled select, or nothing when the column is not in this mode's table.</summary>
        protected string Filter(string key, string emptyText, string? title)
        {
            var f = _vm.Filters.FirstOrDefault(x => x.Key == key);
            if (f is null || f.NotInMode) return "";
            var sb = new StringBuilder("<label");
            if (title is not null) sb.Append(" title=\"").Append(HttpUtility.HtmlAttributeEncode(title)).Append('"');
            sb.Append('>').Append(HttpUtility.HtmlEncode(f.Label)).Append(' ');
            sb.Append(FilterSelect(f, emptyText)).Append("</label>").Append(FilterNote(f));
            return sb.ToString();
        }

        private static string FilterSelect(FilterField f, string emptyText)
        {
            var column = TariffRepository.Filters.First(d => d.Key == f.Key).ColumnCandidates[0];
            var sb = new StringBuilder();
            sb.Append("<select id=\"f_").Append(f.Key).Append("\" name=\"f_").Append(f.Key).Append('"');
            if (f.Disabled) sb.Append(" disabled");
            sb.Append("><option value=\"\">").Append(HttpUtility.HtmlEncode(emptyText)).Append("</option>");
            foreach (var (o, label) in f.Options.Select(o => (o, Global.Master.Label(column, o)))
                                                .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase))
            {
                var enc = HttpUtility.HtmlAttributeEncode(o);
                sb.Append("<option value=\"").Append(enc).Append('"');
                if (o == f.Selected) sb.Append(" selected");
                sb.Append('>').Append(HttpUtility.HtmlEncode(label)).Append("</option>");
            }
            return sb.Append("</select>").ToString();
        }

        private string FilterNote(FilterField f)
        {
            string? note = f.Disabled
                ? f.DisabledNote
                : ddlCarrier.SelectedValue == "" && f.Carriers.Count > 0 && f.Carriers.Count < ddlCarrier.Items.Count - 1
                    ? f.OthersStillMatch
                        ? $"Only {string.Join(", ", f.Carriers)} price by this; other carriers' rates apply to any value."
                        : $"Only {string.Join(", ", f.Carriers)} — choosing a value excludes every other carrier."
                    : null;
            return note is null ? "" : "<div class=\"small muted\">" + HttpUtility.HtmlEncode(note) + "</div>";
        }

        /// <summary>
        /// The "Place" dropdown of one end ("origin" or "dest"), grouped by kind. Disabled until a country is chosen,
        /// because the list is that country's places.
        /// </summary>
        protected string PlaceSelect(string side)
        {
            var origin = side == "origin";
            var country = origin ? _vm.Query.OriginCountry : _vm.Query.DestCountry;
            var places = origin ? _vm.OriginPlaces : _vm.DestPlaces;
            var selected = origin ? _vm.Query.OriginPlaceType + "|" + _vm.Query.OriginPlace
                                  : _vm.Query.DestPlaceType + "|" + _vm.Query.DestPlace;

            var sb = new StringBuilder("<select name=\"").Append(side).Append("_place\"");
            if (country is null)
                return sb.Append(" disabled><option value=\"\">Choose a country first</option></select>").ToString();

            sb.Append("><option value=\"\">Anywhere in ")
              .Append(HttpUtility.HtmlEncode(Global.Master.CountryName(country))).Append("</option>");
            foreach (var g in places.GroupBy(p => p.Group))
            {
                sb.Append("<optgroup label=\"").Append(HttpUtility.HtmlAttributeEncode(g.Key)).Append("\">");
                foreach (var p in g)
                {
                    sb.Append("<option value=\"").Append(HttpUtility.HtmlAttributeEncode(p.Value)).Append('"');
                    if (p.Value == selected) sb.Append(" selected");
                    sb.Append('>').Append(HttpUtility.HtmlEncode(p.Label)).Append("</option>");
                }
                sb.Append("</optgroup>");
            }
            return sb.Append("</select>").ToString();
        }

        /// <summary>Display label of a coded value from the master data (e.g. "LTL" -> "Less than truckload (LTL)").</summary>
        protected static string CodeLabel(string list, string? code) => code is null ? "" : Global.Master.Label(list, code);

        /// <summary>No link for a withheld lane code: the code itself would appear in the URL.</summary>
        protected string LaneUrl(string carrierCode, string? laneCode) =>
            laneCode is null || laneCode == Redactor.Placeholder
                ? "#"
                : ResolveUrl($"~/Lane/Detail/{Uri.EscapeDataString(carrierCode)}/{Uri.EscapeDataString(laneCode)}");

        protected static string Num(decimal? v) => v?.ToString("0.##", Inv) ?? "";

        protected static string Money(decimal? v) => v?.ToString("N2", Inv) ?? "—";

        protected static string Band(decimal? from, decimal? to, string unit) =>
            from is null && to is null ? "" : $"{(from is null ? "0" : Num(from))}–{(to is null ? "∞" : Num(to))} {unit}";

        protected static string Validity(SearchResultRow r) =>
            $"{r.ValidFrom?.ToString("yyyy-MM-dd", Inv)} – {r.ValidTo?.ToString("yyyy-MM-dd", Inv) ?? "open"}";

        protected static string TotalCell(SearchResultRow r) =>
            r.EstimatedTotal is null
                ? "<span class=\"small muted\" style=\"font-weight:normal\">— " + HttpUtility.HtmlEncode(r.NoTotalReason) + "</span>"
                : HttpUtility.HtmlEncode(Money(r.EstimatedTotal) + " " + r.Currency);

        protected static string FuelCell(SearchResultRow r) =>
            r.HasFuelRule
                ? HttpUtility.HtmlEncode(Money(r.FuelAmount))
                : "<span class=\"small muted\">not in tariff</span>";

        /// <summary>The minimum charge lifted the freight above the band rate.</summary>
        protected static bool MinApplied(SearchResultRow r) =>
            r.MinCharge is not null && r.RawFreight is not null && r.RawFreight < r.MinCharge;

        protected static string Rate(decimal? v) => v?.ToString("#,0.00##", Inv) ?? "";

        private static readonly Dictionary<string, string> Bases = new()
        {
            ["PER_KG"] = "per kg", ["PER_100KG"] = "per 100 kg", ["PER_SHIPMENT"] = "per shipment", ["PER_TRUCK"] = "per truck",
            ["PER_KM"] = "per km", ["PER_HOUR"] = "per hour", ["PER_DAY"] = "per day", ["PER_UNIT"] = "per unit", ["PER_CBM"] = "per cbm",
            ["PER_SQM"] = "per sqm", ["PER_CONTAINER"] = "per container", ["PER_TRIP"] = "per trip",
            ["PCT_OF_FREIGHT"] = "% of freight", ["PCT_OF_VALUE"] = "% of value",
        };

        protected static string BasisLabel(string? b) =>
            b is null ? "" : Global.Master.Label("charge_basis", b) is var l && l != b ? l : Bases.TryGetValue(b, out var label) ? label : b;
    }
}
