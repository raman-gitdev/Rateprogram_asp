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

        /// <summary>Menu button class: highlighted on the page it opens (page = file name without .aspx).</summary>
        protected string NavClass(string page) =>
            string.Equals(System.IO.Path.GetFileNameWithoutExtension(Request.AppRelativeCurrentExecutionFilePath), page,
                          StringComparison.OrdinalIgnoreCase) ? "button on" : "button";
    }
}
