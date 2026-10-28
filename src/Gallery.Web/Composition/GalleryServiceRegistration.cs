using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Web.Composition;

public static class GalleryServiceRegistration
{
    private const string VisionHttpClientName = "gallery-vision";
    private const string FFmpegPathSettingName = "Gallery:FFmpegPath";

    public static void Register(IServiceCollection services, IConfiguration configuration, string vaultRoot)
    {
        var galleryRuntimeOptions = CaptureGalleryRuntimeOptions(configuration);
        RegisterVisionHttpClient(services);
        RegisterGalleryService(services, vaultRoot, galleryRuntimeOptions);
    }

    internal static GalleryRuntimeOptions CaptureGalleryRuntimeOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new GalleryRuntimeOptions
        {
            StateDirectory = configuration["Gallery:StateDirectory"],
            FFmpegPath = configuration[FFmpegPathSettingName],
            ToolSearchPaths = DiscoverToolSearchPaths(configuration).ToImmutableArray()
        };
    }

    private static IEnumerable<string> DiscoverToolSearchPaths(IConfiguration configuration)
    {
        var configuredPath = configuration["PATH"] ?? Environment.GetEnvironmentVariable("PATH");
        foreach (var searchPath in ParseSearchPaths(configuredPath)) yield return searchPath;
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var searchPath in ParseSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
            yield return searchPath;
        foreach (var searchPath in ParseSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)))
            yield return searchPath;
    }

    private static IEnumerable<string> ParseSearchPaths(string? searchPathValue) =>
        string.IsNullOrWhiteSpace(searchPathValue) ? [] : searchPathValue.Split(Path.PathSeparator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

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
