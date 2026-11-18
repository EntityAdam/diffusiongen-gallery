namespace Gallery.Core.Services;

public static class GalleryPaths
{
    public static string DefaultApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");

    public static string DefaultVaultDirectory() =>
        Path.Combine(DefaultApplicationDirectory, "galleries", Guid.NewGuid().ToString("N"));
}
