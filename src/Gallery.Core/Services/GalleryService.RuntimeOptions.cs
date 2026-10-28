using Gallery.Core.Models;
using Microsoft.Extensions.Configuration;
using ConfigurationProvider = Gallery.Core.Services.ConfigurationProviders.ConfigurationProvider;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public static GalleryRuntimeOptions CaptureRuntimeOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configurationProvider = new ConfigurationProvider(configuration);
        return configurationProvider.GetGalleryRuntimeOptions();
    }
}
