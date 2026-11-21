namespace Gallery.Core.Features.Media.Export;

internal static class ExportMediaNameFormatter
{
    private const int MaxFileNameLength = 150;

    public static string Format(string name, string contentType) => SafeFileName(name) + (contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        _ => ".png"
    });

    public static string SafeFileName(string name)
    {
        // Archive entries may be extracted on any OS, so both separators are invalid regardless of host.
        var safe = ReplaceUnsafeFileNameCharacters(name).Trim().TrimEnd(' ', '.');
        if (safe.Length == 0) return "image";
        if (IsReservedWindowsDeviceName(safe)) safe = "_" + safe;
        return safe.Length > MaxFileNameLength ? safe[..MaxFileNameLength].TrimEnd(' ', '.') : safe;
    }

    private static string ReplaceUnsafeFileNameCharacters(string name) =>
        new(name.Select(character => IsUnsafeFileNameCharacter(character) ? '_' : character).ToArray());

    private static bool IsUnsafeFileNameCharacter(char character) =>
        char.IsControl(character) || character is '<' or '>' or '"' or ':' or '/' or '\\' or '|' or '?' or '*';

    private static bool IsReservedWindowsDeviceName(string safeName)
    {
        var stem = safeName.Split('.')[0].TrimEnd(' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is (>= '1' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3'));
    }
}
