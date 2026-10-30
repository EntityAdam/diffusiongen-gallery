using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gallery.Stress.Core;

public static class MeasurementSeries
{
    public static string EnvironmentKey(JsonElement row)
    {
        var env = Property(row, "env");
        var values = new[] { "schemaVersion", "gcMode", "gcLatencyMode", "os", "dotnet", "cpus", "ramBytes",
            "vaultDrive", "mediaDrive", "stagingDrive", "power", "defenderExclusion", "gitCommit", "harnessVersion" }
            .Select(key => Text(env, key)).Concat(new[] { Text(row, "vaultDrive"), Text(row, "mediaDrive"),
                Text(row, "pageSize"), Text(row, "measurementTarget"),
                // Resource measurements include the optional waits; keep their scope distinct.
                Property(Property(row, "steps"), "catalog_background").ValueKind == JsonValueKind.Number ? "catalog-wait" : "no-catalog-wait",
                Property(Property(row, "steps"), "drive_idle").ValueKind == JsonValueKind.Number ? "drive-wait" : "no-drive-wait" });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))))[..10].ToLowerInvariant();
    }

    public static string OpenStep(JsonElement row) => Text(row, "measurementTarget") == "passphrase_to_gallery_ready"
        || Property(Property(row, "steps"), "passphrase_to_gallery_ready").ValueKind == JsonValueKind.Number
            ? "passphrase_to_gallery_ready" : "total_to_interactive";

    public static double OpenMilliseconds(JsonElement row)
    {
        var value = Property(Property(row, "steps"), OpenStep(row));
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
    }

    public static string MedianAvailable(IEnumerable<JsonElement> values, bool positiveOnly = false)
    {
        var sorted = values.Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) && (!positiveOnly || number > 0))
            .Select(value => value.GetDouble()).Order().ToArray();
        if (sorted.Length == 0) return "—";
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        return median.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static JsonElement Property(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement row, string key) => Property(row, key).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
        ? "unknown" : Property(row, key).ToString();
}
