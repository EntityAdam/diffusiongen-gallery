using Gallery.Web.Managers.Gateways;
using Gallery.Web.Services.ConfigurationProviders.Sources;

namespace Gallery.Web.Services.ConfigurationProviders;

internal sealed class ConfigurationProvider(IConfiguration configuration)
{
    private readonly SettingsSource settingsSource = new(configuration);

    public VideoToolsOptions GetVideoToolsOptions() => settingsSource.ReadVideoToolsOptions();
}
