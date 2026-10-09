using System.Net;
using Gallery.Components;
using Gallery.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5189");
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.Zero;
});
builder.Services.AddSingleton<VaultStore>();
builder.Services.AddSingleton<GalleryStore>();
builder.Services.AddSingleton(services => new VideoTools(services.GetRequiredService<IConfiguration>()));
builder.Services.AddScoped<VaultSession>();
builder.Services.AddScoped<GalleryService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddHttpClient<VisionService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    });

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
