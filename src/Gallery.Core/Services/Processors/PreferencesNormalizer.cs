using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class PreferencesNormalizer
{
    public static UiPreferences Normalize(UiPreferences preferences)
    {
        var density = UiPreferences.Densities.Contains(preferences.GridDensity)
            ? preferences.GridDensity
            : UiPreferences.ComfortableDensity;
        var autoLock = preferences.AutoLockMinutes switch
        {
            < 0 => UiPreferences.DefaultAutoLockMinutes,
            > 1440 => 1440,
            _ => preferences.AutoLockMinutes
        };
        return preferences with { GridDensity = density, AutoLockMinutes = autoLock };
    }
}
