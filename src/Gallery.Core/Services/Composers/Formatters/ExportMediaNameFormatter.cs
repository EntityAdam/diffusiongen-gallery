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
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        return safe.Length == 0 ? "image" : safe.Length > 150 ? safe[..150] : safe;
    }
}
