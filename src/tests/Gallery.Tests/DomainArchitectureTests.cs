using Gallery.Core;
using Gallery.Models;
using Gallery.Services;
using System.Runtime.CompilerServices;

namespace Gallery.Tests;

public sealed class DomainArchitectureTests
{
    [Fact]
    public void ProductionDomain_IsContainedInTheGalleryAssemblyAndExposesOnlyTheServiceEntryPoint()
    {
        // Arrange
        var galleryAssembly = typeof(GalleryService).Assembly;

        // Act
        var coreReference = galleryAssembly.GetReferencedAssemblies()
            .SingleOrDefault(reference => reference.Name == "Gallery.Core");
        var metadataType = galleryAssembly.GetType("Gallery.Core.ComfyMetadata");
        var duplicateRulesType = galleryAssembly.GetType("Gallery.Core.DuplicateRules");
        var visionServiceType = galleryAssembly.GetType("Gallery.Services.VisionService");
        var videoToolsType = galleryAssembly.GetType("Gallery.Services.VideoTools");
        var galleryStoreType = galleryAssembly.GetType("Gallery.Services.GalleryStore");
        var vaultStoreType = galleryAssembly.GetType("Gallery.Services.VaultStore");
        var vaultSessionType = galleryAssembly.GetType("Gallery.Services.VaultSession");

        // Assert
        Assert.Null(coreReference);
        Assert.Same(galleryAssembly, typeof(ImageRecord).Assembly);
        Assert.NotNull(metadataType);
        Assert.False(metadataType!.IsPublic);
        Assert.NotNull(duplicateRulesType);
        Assert.False(duplicateRulesType!.IsPublic);
        Assert.NotNull(visionServiceType);
        Assert.False(visionServiceType!.IsPublic);
        Assert.NotNull(videoToolsType);
        Assert.False(videoToolsType!.IsPublic);
        Assert.NotNull(galleryStoreType);
        Assert.False(galleryStoreType!.IsPublic);
        Assert.NotNull(vaultStoreType);
        Assert.False(vaultStoreType!.IsPublic);
        Assert.NotNull(vaultSessionType);
        Assert.False(vaultSessionType!.IsPublic);
        Assert.True(typeof(GalleryService).IsPublic);
    }

    [Fact]
    public void VisionGateway_IsStoredInItsRoleOrientedFolder()
    {
        // Arrange
        var testDirectory = Path.GetDirectoryName(SourceFilePath())!;
        var sourceRoot = Directory.GetParent(testDirectory)!.Parent!.FullName;
        var gatewayPath = Path.Combine(sourceRoot, "Managers", "Gateways", "VisionService.cs");
        var oldPath = Path.Combine(sourceRoot, "Services", "VisionService.cs");

        // Act
        var gatewayExists = File.Exists(gatewayPath);
        var oldPathExists = File.Exists(oldPath);

        // Assert
        Assert.True(gatewayExists, $"VisionService should live at {gatewayPath}.");
        Assert.False(oldPathExists, $"VisionService should not remain at {oldPath}.");
    }

    private static string SourceFilePath([CallerFilePath] string path = "") => path;
}
