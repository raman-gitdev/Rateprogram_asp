using Microsoft.Extensions.Hosting.WindowsServices;
using TariffHub.Data;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts in System32; content root must be the install folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});

builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// TARIFFHUB_DB wins when set; otherwise the value from appsettings.Development.json (dev only).
var connectionString = Environment.GetEnvironmentVariable("TARIFFHUB_DB");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = builder.Configuration.GetConnectionString("TariffHub");

builder.Services.AddSingleton(new TariffRepository(connectionString));
builder.Services.AddControllersWithViews();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error");

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/error", () => Results.Problem("An internal error occurred."));
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Search}/{action=Index}");

app.Run();
