using Microsoft.Extensions.Configuration;
using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.ConfigurationProviders.Sources;

namespace Gallery.Core.Services.ConfigurationProviders;

internal sealed class ConfigurationProvider(IConfiguration configuration)
{
    private readonly SettingsSource settingsSource = new(configuration);

    public GalleryRuntimeOptions GetGalleryRuntimeOptions() => settingsSource.ReadGalleryRuntimeOptions();

    internal VideoToolsOptions GetVideoToolsOptions() => settingsSource.ReadVideoToolsOptions();
}
