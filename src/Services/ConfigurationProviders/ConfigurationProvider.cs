using Gallery.Managers.Gateways;
using Gallery.Services.ConfigurationProviders.Sources;

namespace Gallery.Services.ConfigurationProviders;

internal sealed class ConfigurationProvider(IConfiguration configuration)
{
    private readonly SettingsSource settingsSource = new(configuration);

    public VideoToolsOptions GetVideoToolsOptions() => settingsSource.ReadVideoToolsOptions();
}
