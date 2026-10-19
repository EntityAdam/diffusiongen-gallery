using Microsoft.Extensions.Configuration;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.ConfigurationProviders.Sources;

namespace Gallery.Core.Services.ConfigurationProviders;

internal sealed class ConfigurationProvider(IConfiguration configuration)
{
    private readonly SettingsSource settingsSource = new(configuration);

    public VideoToolsOptions GetVideoToolsOptions() => settingsSource.ReadVideoToolsOptions();
}
