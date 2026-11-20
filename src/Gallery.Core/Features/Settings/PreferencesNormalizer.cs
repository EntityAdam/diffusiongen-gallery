using Gallery.Core.Models;

namespace Gallery.Core.Features.Settings;

internal static class PreferencesNormalizer
{
    private const int MaxAutoLockMinutes = 1440;

    public static UiPreferences Normalize(UiPreferences uiPreferences) => uiPreferences with
    {
        GridDensity = NormalizeDensity(uiPreferences.GridDensity),
        ThumbnailScale = Math.Clamp(uiPreferences.ThumbnailScale, UiPreferences.MinThumbnailScale, UiPreferences.MaxThumbnailScale),
        ExperimentalFeatures = uiPreferences.ExperimentalFeatures ?? System.Collections.Immutable.ImmutableDictionary<string, bool>.Empty,
        AutoLockMinutes = NormalizeAutoLockMinutes(uiPreferences.AutoLockMinutes),
        ActiveTool = NormalizeActiveTool(uiPreferences.ActiveTool)
    };

    private static string NormalizeDensity(string gridDensity) =>
        UiPreferences.Densities.Contains(gridDensity) ? gridDensity : UiPreferences.DetailDensity;

    private static int NormalizeAutoLockMinutes(int autoLockMinutes) => autoLockMinutes switch
    {
        < 0 => UiPreferences.DefaultAutoLockMinutes,
        > MaxAutoLockMinutes => MaxAutoLockMinutes,
        _ => autoLockMinutes
    };

    private static string NormalizeActiveTool(string activeTool) =>
        UiPreferences.ActiveTools.Contains(activeTool) ? activeTool : "";
}
