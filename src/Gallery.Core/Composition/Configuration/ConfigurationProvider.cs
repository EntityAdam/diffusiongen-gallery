using Gallery.Core.Features.Ingestion.Video;
using Microsoft.Extensions.Configuration;

namespace Gallery.Core.Composition.Configuration;

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
