namespace Gallery.Web.Services.DataManagers.Models;

// The caller owns the decoded PNG buffer and clears it after use.
internal sealed record ImageThumbnail(byte[] PngBytes);
