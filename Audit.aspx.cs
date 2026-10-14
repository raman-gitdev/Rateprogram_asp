using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.UI;
using TariffHub.Models;

namespace TariffHub
{
    /// <summary>
    /// Search log: the most recent searches from the site (tariff.search_audit, script 78), newest first,
    /// with a mode filter. Read live.
    /// </summary>
    public partial class Audit : Page
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const int MaxRows = 300;

        protected void Page_Load(object sender, EventArgs e)
        {
            var mode = Request.QueryString["mode"];
            if (mode is not ("Ground" or "Air" or "Ocean")) mode = null;

            var rows = Global.Audit.Recent(MaxRows, mode);
            var (total, today, ips) = Global.Audit.Summary();
            var sb = new StringBuilder();

            string St(long v, string l) => $"<div class=\"stat\"><b>{v.ToString("N0", Inv)}</b><span>{l}</span></div>";
            sb.Append("<div class=\"cov\"><div class=\"totals\">")
              .Append(St(total, "searches logged")).Append(St(today, "today")).Append(St(ips, "distinct IPs")).Append("</div></div>");

            // mode filter
            string Tab(string? m, string label) =>
                $"<a class=\"button{(mode == m ? " on" : "")}\" href=\"{ResolveUrl("~/Audit.aspx")}{(m is null ? "" : "?mode=" + m)}\">{label}</a>";
            sb.Append("<p class=\"seg\" style=\"margin:10px 0\">")
              .Append(Tab(null, "All")).Append(Tab("Ground", "Ground")).Append(Tab("Air", "Air")).Append(Tab("Ocean", "Ocean")).Append("</p>");

            if (rows.Count == 0)
            {
                sb.Append("<p class=\"muted\">No searches logged yet.</p>");
                litAudit.Text = sb.ToString();
                return;
            }

            sb.Append("<div class=\"scroll\"><table><thead><tr>")
              .Append("<th>When</th><th>Caller IP</th><th>Mode</th><th>Carrier</th><th>From</th><th>To</th>")
              .Append("<th>Service</th><th class=\"num\">Weight</th><th class=\"num\">Volume</th><th class=\"num\">Results</th><th>Filters</th>")
              .Append("</tr></thead><tbody>");
            foreach (var r in rows)
            {
                sb.Append("<tr><td class=\"nw\" title=\"").Append(E(r.UserAgent)).Append("\">")
                  .Append(E(r.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv))).Append("</td>")
                  .Append("<td class=\"nw\">").Append(E(r.ClientIp)).Append(Fwd(r)).Append("</td>")
                  .Append("<td>").Append(E(r.Mode)).Append("</td>")
                  .Append("<td>").Append(E(r.Carrier)).Append("</td>")
                  .Append("<td class=\"nw\">").Append(Place(r.OriginCountry, r.OriginPlace)).Append("</td>")
                  .Append("<td class=\"nw\">").Append(Place(r.DestCountry, r.DestPlace)).Append("</td>")
                  .Append("<td>").Append(E(r.Service)).Append("</td>")
                  .Append("<td class=\"num\">").Append(r.WeightKg is { } w ? E(w.ToString("0.##", Inv) + " kg") : "").Append("</td>")
                  .Append("<td class=\"num\">").Append(r.VolumeCbm is { } v ? E(v.ToString("0.##", Inv) + " m³") : "").Append("</td>")
                  .Append("<td class=\"num\">").Append(r.ResultCount?.ToString("N0", Inv) ?? "—").Append("</td>")
                  .Append("<td class=\"codes\" style=\"max-width:320px\">").Append(E(r.Filters)).Append("</td>")
                  .Append("</tr>");
            }
            sb.Append("</tbody></table></div>");
            sb.Append("<p class=\"small muted\">Newest first, up to ").Append(MaxRows)
              .Append(" shown. Times are local. Hover a row's time for the browser's user agent.</p>");
            litAudit.Text = sb.ToString();
        }

        private static string E(string? v) => HttpUtility.HtmlEncode(v ?? "");

        private static string Place(string? country, string? place)
        {
            var c = string.IsNullOrWhiteSpace(country) ? "" : country!.Trim();
            var p = string.IsNullOrWhiteSpace(place) ? "" : place!.Trim();
            var s = string.Join(" ", new[] { c, p }.Where(x => x.Length > 0));
            return s.Length == 0 ? "<span class=\"muted\">any</span>" : E(s);
        }

        private static string Fwd(SearchAudit r) =>
            string.IsNullOrWhiteSpace(r.ForwardedFor) || r.ForwardedFor == r.ClientIp
                ? "" : " <span class=\"small muted\" title=\"X-Forwarded-For\">via</span>";
    }
}
