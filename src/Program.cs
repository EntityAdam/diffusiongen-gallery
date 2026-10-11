using System.Net;
using Gallery.Components;
using Gallery.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5189");
builder.WebHost.UseStaticWebAssets();
var configuredVaultRoot = builder.Configuration["Gallery:DataDirectory"];
var vaultRoot = string.IsNullOrWhiteSpace(configuredVaultRoot)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionGenGallery02")
    : configuredVaultRoot;
var normalizedVaultRoot = Path.GetFullPath(vaultRoot);
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.Zero;
});
builder.Services.AddGalleryDomain(normalizedVaultRoot);

var app = builder.Build();
// Binding and Host validation both matter: reject DNS rebinding and accidental LAN exposure.
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    var host = context.Request.Host.Host;
    var origin = context.Request.Headers.Origin.ToString();
    if (remote is null || !IPAddress.IsLoopback(remote)
        || !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
        || (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || !uri.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase))))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
    await next();
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
