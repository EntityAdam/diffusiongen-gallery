using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Services;

internal static class GalleryServiceCollectionExtensions
{
    public static IServiceCollection AddGalleryDomain(this IServiceCollection services, string vaultRoot)
    {
        services.AddSingleton(new VaultStore(vaultRoot));
        services.AddSingleton(provider => new GalleryStore(
            Path.Combine(provider.GetRequiredService<VaultStore>().Root, "gallery.db")));
        services.AddSingleton(provider =>
            new VideoTools(CreateVideoToolsOptions(provider.GetRequiredService<IConfiguration>())));
        services.AddScoped<VaultSession>();
        services.AddScoped<LibraryService>();
        services.AddHttpClient<VisionService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });
        services.AddScoped<GalleryService>(provider => new GalleryService(
            provider.GetRequiredService<GalleryStore>(),
            provider.GetRequiredService<VaultStore>(),
            provider.GetRequiredService<VaultSession>(),
            provider.GetRequiredService<VideoTools>(),
            provider.GetRequiredService<VisionService>(),
            provider.GetRequiredService<LibraryService>()));
        return services;
    }

    private static VideoToolsOptions CreateVideoToolsOptions(IConfiguration configuration)
    {
        var searchPaths = new List<string>();
        AddSearchPaths(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            AddSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }

        return new VideoToolsOptions(configuration["Gallery:FFmpegPath"], searchPaths);

        void AddSearchPaths(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            searchPaths.AddRange(value.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
