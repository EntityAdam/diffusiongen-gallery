using System.Collections.Immutable;
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
        var searchPaths = new List<string>();
        AddSearchPaths(configuration["PATH"] ?? Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            AddSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        var options = new GalleryRuntimeOptions
        {
            StateDirectory = configuration["Gallery:StateDirectory"],
            FFmpegPath = configuration["Gallery:FFmpegPath"],
            ToolSearchPaths = searchPaths.ToImmutableArray()
        };
        services.AddHttpClient(VisionHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });
        services.AddScoped(provider => GalleryService.Create(vaultRoot, options,
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(VisionHttpClientName)));

        void AddSearchPaths(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                searchPaths.AddRange(value.Split(Path.PathSeparator,
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
