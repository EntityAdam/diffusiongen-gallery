using Gallery.Core.Extractors;
using Gallery.Core.Processors;
using Gallery.Managers.Gateways;
using Gallery.Services.DataManagers;
using Gallery.Services.Sessions;
using Gallery.Core;
using Gallery.Models;
using Gallery.Services;
using System.Runtime.CompilerServices;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
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
        var metadataType = galleryAssembly.GetType("Gallery.Core.Extractors.ComfyMetadata");
        var duplicateRulesType = galleryAssembly.GetType("Gallery.Core.Processors.DuplicateRules");
        var visionServiceType = galleryAssembly.GetType("Gallery.Managers.Gateways.VisionService");
        var videoToolsType = galleryAssembly.GetType("Gallery.Managers.Gateways.VideoTools");
        var galleryStoreType = galleryAssembly.GetType("Gallery.Services.DataManagers.GalleryStore");
        var vaultStoreType = galleryAssembly.GetType("Gallery.Services.DataManagers.VaultStore");
        var vaultSessionType = galleryAssembly.GetType("Gallery.Services.Sessions.VaultSession");

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

    [Fact]
    public void DomainModels_WhenOrganized_ThenLiveBeneathManagers()
    {
        // Arrange
        var testDirectory = Path.GetDirectoryName(SourceFilePath())!;
        var sourceRoot = Directory.GetParent(testDirectory)!.Parent!.FullName;
        var modelsPath = Path.Combine(sourceRoot, "Managers", "Models");
        var oldModelsPath = Path.Combine(sourceRoot, "Gallery.Core", "Models");

        // Act
        var modelsExist = Directory.Exists(modelsPath);
        var oldModelsExist = Directory.Exists(oldModelsPath);

        // Assert
        Assert.True(modelsExist, $"Domain models belong under Managers at {modelsPath}.");
        Assert.False(oldModelsExist, $"The former top-level model folder {oldModelsPath} is absent.");
    }

    private static string SourceFilePath([CallerFilePath] string path = "") => path;
}
