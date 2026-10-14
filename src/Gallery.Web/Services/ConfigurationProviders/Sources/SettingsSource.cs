using Gallery.Web.Managers.Gateways;

namespace Gallery.Web.Services.ConfigurationProviders.Sources;

internal sealed class SettingsSource(IConfiguration configuration)
{
    public VideoToolsOptions ReadVideoToolsOptions()
    {
        var searchPaths = new List<string>();
        AddSearchPaths(configuration["PATH"] ?? Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            AddSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddSearchPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        return new VideoToolsOptions(configuration["Gallery:FFmpegPath"], searchPaths);

        void AddSearchPaths(string? searchPathValue)
        {
            if (string.IsNullOrWhiteSpace(searchPathValue)) return;
            searchPaths.AddRange(searchPathValue.Split(Path.PathSeparator,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
