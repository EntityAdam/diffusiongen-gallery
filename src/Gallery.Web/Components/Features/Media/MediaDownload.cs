using Gallery.Core.Models;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Features.Media;

internal static class MediaDownload
{
    internal static async Task SendAsync(IJSRuntime js, ExportFile file)
    {
        using var reference = new DotNetStreamReference(file.OpenRead());
        await js.InvokeVoidAsync("galleryMedia.download", reference, file.FileName, file.ContentType);
    }
}
