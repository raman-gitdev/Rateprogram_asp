using System;
using System.Web.UI;

namespace TariffHub
{
    public partial class SiteMaster : MasterPage
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            Page.Title = string.IsNullOrEmpty(Page.Title) ? "TariffHub" : Page.Title + " - TariffHub";
        }
    }
}
