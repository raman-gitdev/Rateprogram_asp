using System;
using System.Configuration;
using System.Web;
using TariffHub.Data;
using TariffHub.Logic;

namespace TariffHub
{
    public class Global : HttpApplication
    {
        public static TariffRepository Repository { get; private set; } = null!;
        public static Redactor Redactor { get; private set; } = null!;
        public static MasterData Master { get; private set; } = null!;

        private static bool _localOnly;

        protected void Application_Start(object sender, EventArgs e)
        {
            // TARIFFHUB_DB wins when set; otherwise the value from Secrets.config.
            var connectionString = Environment.GetEnvironmentVariable("TARIFFHUB_DB");
            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = ConfigurationManager.AppSettings["TariffHub.ConnectionString"];

            Repository = new TariffRepository(connectionString);
            Redactor = new Redactor(ConfigurationManager.AppSettings["TariffHub.RedactTerms"]);
            // Reference data (countries, subdivisions, labels, carrier districts) from db/master/data/*.json.
            Master = new MasterData(System.IO.Path.Combine(HttpRuntime.AppDomainAppPath, "db", "master", "data"));
            _localOnly = !string.Equals(ConfigurationManager.AppSettings["TariffHub.LocalOnly"], "false", StringComparison.OrdinalIgnoreCase);
        }

        protected void Application_BeginRequest(object sender, EventArgs e)
        {
            if (_localOnly && !Request.IsLocal)
            {
                Response.StatusCode = 403;
                CompleteRequest();
            }
        }
    }
}
