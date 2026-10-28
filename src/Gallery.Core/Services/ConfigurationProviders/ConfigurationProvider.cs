using Microsoft.Extensions.Configuration;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.ConfigurationProviders.Sources;

namespace Gallery.Core.Services.ConfigurationProviders;

internal sealed class ConfigurationProvider
{
    private readonly SettingsSource settingsSource;

    public ConfigurationProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        settingsSource = new SettingsSource(configuration);
    }

    internal VideoToolsOptions GetVideoToolsOptions() => settingsSource.ReadVideoToolsOptions();
}
