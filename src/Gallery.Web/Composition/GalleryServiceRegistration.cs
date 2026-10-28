using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Web.Composition;

public static class GalleryServiceRegistration
{
    private const string VisionHttpClientName = "gallery-vision";

    public static void Register(IServiceCollection services, IConfiguration configuration, string vaultRoot)
    {
        var galleryRuntimeOptions = GalleryService.CaptureRuntimeOptions(configuration);
        RegisterVisionHttpClient(services);
        RegisterGalleryService(services, vaultRoot, galleryRuntimeOptions);
    }

    private static void RegisterVisionHttpClient(IServiceCollection services) =>
        services.AddHttpClient(VisionHttpClientName, httpClient => httpClient.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });

    private static void RegisterGalleryService(IServiceCollection services, string vaultRoot,
        GalleryRuntimeOptions galleryRuntimeOptions) =>
        services.AddScoped(serviceProvider => GalleryService.Create(vaultRoot, galleryRuntimeOptions,
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(VisionHttpClientName)));
}
