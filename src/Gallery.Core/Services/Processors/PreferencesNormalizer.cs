using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class PreferencesNormalizer
{
    private const int MaxAutoLockMinutes = 1440;

    public static UiPreferences Normalize(UiPreferences uiPreferences) => uiPreferences with
    {
        GridDensity = NormalizeDensity(uiPreferences.GridDensity),
        AutoLockMinutes = NormalizeAutoLockMinutes(uiPreferences.AutoLockMinutes),
        ToolboxTool = NormalizeToolboxTool(uiPreferences.ToolboxTool),
        ToolboxX = Coordinate(uiPreferences.ToolboxX), ToolboxY = Coordinate(uiPreferences.ToolboxY)
    };

    private static string NormalizeDensity(string gridDensity) =>
        UiPreferences.Densities.Contains(gridDensity) ? gridDensity : UiPreferences.DetailDensity;

    private static int NormalizeAutoLockMinutes(int autoLockMinutes) => autoLockMinutes switch
    {
        < 0 => UiPreferences.DefaultAutoLockMinutes,
        > MaxAutoLockMinutes => MaxAutoLockMinutes,
        _ => autoLockMinutes
    };

    private static string NormalizeToolboxTool(string toolboxTool) =>
        UiPreferences.ToolboxTools.Contains(toolboxTool) ? toolboxTool : "";

    private static double? Coordinate(double? value) =>
        value is double coordinate && double.IsFinite(coordinate) ? Math.Clamp(coordinate, 0, 100_000) : null;
}
