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
        var tool = UiPreferences.ToolboxTools.Contains(preferences.ToolboxTool) ? preferences.ToolboxTool : "";
        return preferences with
        {
            GridDensity = density, AutoLockMinutes = autoLock, ToolboxTool = tool,
            ToolboxX = Coordinate(preferences.ToolboxX), ToolboxY = Coordinate(preferences.ToolboxY)
        };
    }

    private static double? Coordinate(double? value) =>
        value is double coordinate && double.IsFinite(coordinate) ? Math.Clamp(coordinate, 0, 100_000) : null;
}
