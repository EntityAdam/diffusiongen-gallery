using SixLabors.ImageSharp.Formats;

namespace Gallery.Web.Services.Processors;

/// <summary>Image formats whose original bytes are stored unchanged and that browsers can display directly.</summary>
internal static class ImageContentFormats
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";
    public const string Mp4 = "video/mp4";

    public static string RequireSupportedContentType(IImageFormat? decodedFormat) =>
        decodedFormat?.DefaultMimeType switch
        {
            Png => Png,
            Jpeg => Jpeg,
            WebP => WebP,
            _ => throw new InvalidOperationException("Only PNG, JPEG and WebP images are supported.")
        };

    public static string FileExtension(string contentType) => contentType switch
    {
        Jpeg => ".jpg",
        WebP => ".webp",
        Mp4 => ".mp4",
        _ => ".png"
    };

    public static string DataUrlPrefix(string contentType) => $"data:{contentType};base64,";
}
