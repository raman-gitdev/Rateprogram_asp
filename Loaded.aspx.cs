using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Web.Caching;
using System.Web.Script.Serialization;
using System.Web.UI;
using TariffHub.Data;
using TariffHub.Models;

namespace TariffHub
{
    /// <summary>
    /// Loaded tariffs: the coverage map (country pairs per carrier, Ground / Air) and one row per carrier with what is loaded.
    /// Read live from the rate tables; the result is kept 10 minutes (the queries read every rate row), "?refresh=1" reads again.
    /// </summary>
    public partial class Loaded : Page
    {
        private const string CacheKey = "TariffHub.LoadedTariffs";
        private static readonly object BuildLock = new();

        /// <summary>The page data as JSON, written into the page script.</summary>
        protected string DataJson { get; private set; } = "null";

        protected void Page_Load(object sender, EventArgs e)
        {
            try
            {
                var refresh = Request.QueryString["refresh"] == "1";
                var json = refresh ? null : Cache[CacheKey] as string;
                if (json is null)
                {
                    lock (BuildLock)
                    {
                        json = refresh ? null : Cache[CacheKey] as string;
                        if (json is null)
                        {
                            json = Build();
                            Cache.Insert(CacheKey, json, null, DateTime.UtcNow.AddMinutes(10), System.Web.Caching.Cache.NoSlidingExpiration);
                        }
                    }
                }
                DataJson = json;
            }
            catch (Exception ex)
            {
                litError.Text = "<div class=\"err\">Could not read the rate tables: " + HttpUtility.HtmlEncode(ex.Message) + "</div>";
            }
        }

        /// <summary>Version-stamped URL of a file in Content/coverage, so browsers never keep a stale copy.</summary>
        protected string Asset(string file)
        {
            var url = "~/Content/coverage/" + file;
            var ticks = System.IO.File.GetLastWriteTimeUtc(Server.MapPath(url)).Ticks;
            return ResolveUrl(url) + "?v=" + ticks.ToString(CultureInfo.InvariantCulture);
        }

        private static string Build()
        {
            var repo = Global.Repository;
            var ground = repo.CoverageLanes(TariffMode.Ground);
            var air = repo.CoverageLanes(TariffMode.Air);
            var ocean = repo.OceanCoverage();
            var zip = repo.CoveragePostcodePairs();
            var carriers = repo.LoadedCarriers();
            var oceanCarriers = repo.OceanLoaded();

            static bool Placed(TariffRepository.CoverageLane l) => l.O is not null && l.D is not null;
            static object[] Lane(TariffRepository.CoverageLane l) => [l.C, l.O!, l.D!, l.Lvl, l.N, l.Rows];
            static string Day(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

            var codes = ground.Concat(air).Concat(ocean).Where(Placed).SelectMany(l => new[] { l.O!, l.D! }).Distinct();
            // Ocean carriers map into the same carrier-table columns: rate rows = FCL + LCL lines, "zone chart" = 0,
            // "extra charges" = accessorials, "transit" column carries the ORI (rate-increase) rows.
            static string OceanServices(OceanLoadedRow r) =>
                string.Join(", ", new[] { r.FclRows > 0 ? "FCL" : null, r.LclRows > 0 ? "LCL" : null }.Where(x => x is not null));
            var data = new Dictionary<string, object>
            {
                ["built"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["names"] = codes.ToDictionary(c => c, c => Global.Master.CountryName(c)),
                ["ground"] = ground.Where(Placed).Select(Lane).ToList(),
                ["air"] = air.Where(Placed).Select(Lane).ToList(),
                ["ocean"] = ocean.Where(Placed).Select(Lane).ToList(),
                ["unplaced"] = ground.Where(l => !Placed(l)).Select(l => new object[] { "Ground", l.C, l.Rows })
                    .Concat(air.Where(l => !Placed(l)).Select(l => new object[] { "Air", l.C, l.Rows })).ToList(),
                ["zip"] = zip.Select(z => new object[] { z.O, z.D, z.Lanes, z.FromAreas, z.ToAreas, z.Carriers }).ToList(),
                ["carriers"] = carriers.Select(r => new object[]
                {
                    r.Mode, r.CarrierCode, r.RateRows, r.FreightRows, r.Services ?? "", r.Currencies ?? "",
                    Day(r.ValidFrom), Day(r.ValidTo), Day(r.LastLoaded?.ToLocalTime()), r.ZoneRows, r.ExtraRows, r.TransitRows,
                }).Concat(oceanCarriers.Select(r => new object[]
                {
                    "Ocean", r.CarrierCode, r.FclRows + r.LclRows, r.FclRows + r.LclRows, OceanServices(r), r.Currencies ?? "",
                    Day(r.ValidFrom), Day(r.ValidTo), Day(r.LastLoaded?.ToLocalTime()), 0L, r.AccessorialRows, r.OriRows,
                })).ToList(),
            };
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(data);
            return json.Replace("</", "<\\/");   // never close the script element early
        }
    }
}
