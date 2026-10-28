using Gallery.Web.Composition;
using System.Net;
using Gallery.Web.Components;
using Gallery.Core.Services;
using ShadCn.Blazor.Components;

var webApplicationBuilder = WebApplication.CreateBuilder(args);
webApplicationBuilder.WebHost.UseUrls(webApplicationBuilder.Configuration["urls"] ?? "http://127.0.0.1:5189");
webApplicationBuilder.WebHost.UseStaticWebAssets();
var configuredVaultRoot = webApplicationBuilder.Configuration["Gallery:DataDirectory"];
var vaultRoot = string.IsNullOrWhiteSpace(configuredVaultRoot)
    ? GalleryService.DefaultVaultDirectory(GalleryService.GenerateVaultName())
    : configuredVaultRoot;
var normalizedVaultRoot = Path.GetFullPath(vaultRoot);
webApplicationBuilder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.Zero;
});
GalleryServiceRegistration.Register(webApplicationBuilder.Services, webApplicationBuilder.Configuration, normalizedVaultRoot);
webApplicationBuilder.Services.AddShadCnBlazorComponents();

var webApplication = webApplicationBuilder.Build();
// Binding and Host validation both matter: reject DNS rebinding and accidental LAN exposure.
webApplication.Use(async (httpContext, next) =>
{
    if (IsUntrustedLocalRequest(httpContext.Connection.RemoteIpAddress, httpContext.Request.Host,
        httpContext.Request.Headers.Origin.ToString(), httpContext.Request.Scheme))
    {
        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    httpContext.Response.Headers.CacheControl = "no-store";
    httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
    httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
    httpContext.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
    await next();
});
webApplication.UseAntiforgery();
webApplication.MapStaticAssets();
webApplication.MapRazorComponents<App>().AddInteractiveServerRenderMode();
webApplication.Run();

static bool IsUntrustedLocalRequest(IPAddress? remote, HostString host, string origin, string scheme) =>
    remote is null || !IPAddress.IsLoopback(remote)
        || !(host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host.Host, out var ip) && IPAddress.IsLoopback(ip))
        || (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !uri.Authority.Equals(host.Value, StringComparison.OrdinalIgnoreCase)
            || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase)));
