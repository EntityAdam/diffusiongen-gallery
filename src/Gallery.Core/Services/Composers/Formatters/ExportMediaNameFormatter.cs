namespace Gallery.Core.Services.Composers.Formatters;

internal static class ExportMediaNameFormatter
{
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
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToArray();
        var safe = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd(' ', '.');
        if (safe.Length == 0) return "image";
        var stem = safe.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is (>= '1' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3')
            safe = "_" + safe;
        return safe.Length > 150 ? safe[..150].TrimEnd(' ', '.') : safe;
    }
}
