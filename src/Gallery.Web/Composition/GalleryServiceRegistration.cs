using System.Collections.Immutable;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;
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
        RegisterGalleryScope(services, vaultRoot, galleryRuntimeOptions);
        RegisterCapabilities(services);
        services.AddScoped<Gallery.Web.Components.OrganizationHistory>();
    }

    internal static GalleryRuntimeOptions CaptureGalleryRuntimeOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new GalleryRuntimeOptions
        {
            StateDirectory = configuration["Gallery:StateDirectory"],
            CatalogBackend = ParseCatalogBackend(configuration["Gallery:CatalogBackend"]),
            FFmpegPath = configuration[FFmpegPathSettingName],
            ToolSearchPaths = DiscoverToolSearchPaths(configuration).ToImmutableArray()
        };
    }

    private static CatalogBackend ParseCatalogBackend(string? value) => string.IsNullOrWhiteSpace(value)
        ? CatalogBackend.Sqlite
        : Enum.TryParse<CatalogBackend>(value, ignoreCase: true, out var backend) && Enum.IsDefined(backend)
            ? backend
            : throw new InvalidOperationException("Gallery:CatalogBackend must be Sqlite or Turso.");

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

    private static void RegisterGalleryScope(IServiceCollection services, string vaultRoot,
        GalleryRuntimeOptions galleryRuntimeOptions) =>
        services.AddScoped(serviceProvider => GalleryScope.Create(vaultRoot, galleryRuntimeOptions,
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(VisionHttpClientName)));

    internal static void RegisterCapabilities(IServiceCollection services)
    {
        services.AddScoped(provider => new GalleryService(provider.GetRequiredService<GalleryScope>()));
        services.AddScoped<IGalleryBackup>(provider => provider.GetRequiredService<GalleryScope>().Backup);
        services.AddScoped<IGalleryCatalog>(provider => provider.GetRequiredService<GalleryScope>().Catalog);
        services.AddScoped<IGalleryCollections>(provider => provider.GetRequiredService<GalleryScope>().Collections);
        services.AddScoped<IGallerySettings>(provider => provider.GetRequiredService<GalleryScope>().Settings);
        services.AddScoped<IGalleryDriveOperations>(provider => provider.GetRequiredService<GalleryScope>().DriveOperations);
        services.AddScoped<IGalleryDuplicates>(provider => provider.GetRequiredService<GalleryScope>().Duplicates);
        services.AddScoped<IGalleryRecordEditor>(provider => provider.GetRequiredService<GalleryScope>().Records);
        services.AddScoped<IGalleryIngestion>(provider => provider.GetRequiredService<GalleryScope>().Ingestion);
        services.AddScoped<IGalleryVault>(provider => provider.GetRequiredService<GalleryScope>().Vault);
        services.AddScoped<IGalleryMedia>(provider => provider.GetRequiredService<GalleryScope>().Media);
        services.AddScoped<IGalleryMediaMaintenance>(provider => provider.GetRequiredService<GalleryScope>().MediaMaintenance);
        services.AddScoped<IGalleryRestore>(provider => provider.GetRequiredService<GalleryScope>().Restore);
        services.AddScoped<IGalleryStorage>(provider => provider.GetRequiredService<GalleryScope>().Storage);
        services.AddScoped<IGalleryTags>(provider => provider.GetRequiredService<GalleryScope>().Tags);
        services.AddScoped<IGalleryVision>(provider => provider.GetRequiredService<GalleryScope>().Vision);
        services.AddScoped<WorkspaceGalleryServices>();
    }
}
