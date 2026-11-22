using Gallery.Core.Features.Media.Removal.Managers.Validators;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Gallery.Core.Features.Browsing;
using Gallery.Core.Features.Browsing.Filtering;
using Gallery.Core.Features.Curation;
using Gallery.Core.Features.Duplicates;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Features.Ingestion.Managers.Reporters;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainArchitectureTests
{
    [Fact]
    public void ProductionAssemblies_KeepDomainBehaviorBehindAnIndependentCoreEntry()
    {
        var core = typeof(GalleryScope).Assembly;
        var web = typeof(App).Assembly;

        Assert.Equal("Gallery.Core", core.GetName().Name);
        Assert.Equal("Gallery.Web", web.GetName().Name);
        Assert.NotSame(web, core);
        Assert.Contains(web.GetReferencedAssemblies(), reference => reference.Name == "Gallery.Core");
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            reference.Name == "Gallery.Web" || reference.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.All(new[] { typeof(DuplicateRules), typeof(LibraryRules), typeof(ReviewDecision),
            typeof(PromptRules), typeof(RemovalRules), typeof(GalleryFilterProcessor), typeof(ComfyMetadata),
            typeof(PerceptualImageHasher), typeof(VisionService), typeof(VideoTools),
            typeof(GalleryStore), typeof(VaultStore), typeof(VaultSession), typeof(IngestionTimings) }, type =>
        {
            Assert.Same(core, type.Assembly);
            Assert.False(type.IsPublic);
        });
    }

    [Fact]
    public void CoreSource_UsesExplicitRoleFoldersWithoutShared()
    {
        var coreSource = Path.Combine(SourceRoot(), "Gallery.Core");

        Assert.False(Directory.Exists(Path.Combine(coreSource, "Shared")));
        Assert.True(File.Exists(Path.Combine(coreSource, "Features", "Validators", "OperationChoiceValidator.cs")));
        Assert.True(File.Exists(Path.Combine(coreSource, "Services", "GalleryDisplay.cs")));
        Assert.True(File.Exists(Path.Combine(coreSource, "Features", "Formatters", "GalleryDisplay.cs")));
        var core = typeof(GalleryScope).Assembly;
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.GalleryException"));
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.GalleryBusinessException"));
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.GalleryTechnicalException"));
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.LogEventBase"));
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.ContextualData"));
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.ExceptionCause"));
        Assert.NotNull(core.GetType("Gallery.Core.Exceptions.Severity"));
        Assert.NotNull(core.GetType("Gallery.Core.Services.GalleryDisplay"));
    }

    [Fact]
    public void VaultSource_SeparatesAccessAndPersistenceFromOrchestration()
    {
        var vaultRoot = Path.Combine(SourceRoot(), "Gallery.Core", "Features", "Vaults");
        foreach (var (folder, name) in new[]
        {
            ("Security", "VaultAccess"),
            ("Security", "VaultCredentialKind"),
            ("Persistence", "VaultHistory"),
            ("Persistence", "VaultStore")
        })
        {
            Assert.True(File.Exists(Path.Combine(vaultRoot, folder, name + ".cs")));
            Assert.False(File.Exists(Path.Combine(vaultRoot, name + ".cs")));
            Assert.NotNull(typeof(GalleryScope).Assembly.GetType("Gallery.Core.Features.Vaults." + name));
        }
    }

    [Fact]
    public void ProjectBoundary_HasNoHostFriendAccessOrLinkedDomainSource()
    {
        var sourceRoot = SourceRoot();
        var coreProject = XDocument.Load(Path.Combine(sourceRoot, "Gallery.Core", "Gallery.Core.csproj"));
        var migrationsProject = XDocument.Load(Path.Combine(sourceRoot, "Gallery.Migrations", "Gallery.Migrations.csproj"));
        var webProject = XDocument.Load(Path.Combine(sourceRoot, "Gallery.Web", "Gallery.Web.csproj"));

        Assert.Equal("Microsoft.NET.Sdk", coreProject.Root!.Attribute("Sdk")!.Value);
        Assert.EndsWith("Gallery.Migrations.csproj",
            Assert.Single(coreProject.Descendants("ProjectReference")).Attribute("Include")!.Value);
        Assert.Empty(migrationsProject.Descendants("ProjectReference"));
        Assert.Empty(migrationsProject.Descendants("FrameworkReference"));
        Assert.Empty(coreProject.Descendants("FrameworkReference"));
        Assert.DoesNotContain(coreProject.Descendants("InternalsVisibleTo"),
            friend => friend.Attribute("Include")!.Value == "Gallery.Web");
        Assert.Empty(coreProject.Descendants("Compile"));
        Assert.Empty(webProject.Descendants("Compile"));
        Assert.EndsWith("Gallery.Core.csproj",
            Assert.Single(webProject.Descendants("ProjectReference")).Attribute("Include")!.Value);
    }

    [Fact]
    public void EntryContracts_UseCanonicalCoreModelsWithoutExposingInteriorComponents()
    {
        var core = typeof(GalleryScope).Assembly;
        var contracts = typeof(GalleryScope).GetProperties()
            .Select(property => property.PropertyType)
            .Append(typeof(GalleryScope));
        foreach (var method in contracts.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)))
        {
            AssertPublicContract(method.ReturnType, core);
            foreach (var parameter in method.GetParameters()) AssertPublicContract(parameter.ParameterType, core);
        }
        Assert.DoesNotContain(typeof(App).Assembly.GetTypes(),
            type => type.Namespace == "Gallery.Web.Models");
    }

    [Theory]
    [InlineData(typeof(ImageRecordEdit))]
    [InlineData(typeof(ReviewState))]
    public void PublicEditAndReviewContracts_ExposeStateWithoutDomainOperations(Type type)
    {
        Assert.DoesNotContain(type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            method => method.Name is "ToRecord" or "Capture");
        Assert.Same(typeof(GalleryScope).Assembly, type.Assembly);
    }

    [Fact]
    public void DriveOperationConflictException_IsPublic()
    {
        Assert.True(typeof(DriveOperationConflictException).IsPublic);
    }

    private static void AssertPublicContract(Type type, Assembly core)
    {
        if (type.HasElementType) AssertPublicContract(type.GetElementType()!, core);
        foreach (var argument in type.GenericTypeArguments) AssertPublicContract(argument, core);
        if (type.Assembly != core) return;
        Assert.True(type.IsPublic || type.IsNestedPublic, $"Public entry exposes internal {type.FullName}.");
        Assert.DoesNotContain(".Infrastructure", type.Namespace ?? "");
        Assert.DoesNotContain(".Features", type.Namespace ?? "");
        Assert.DoesNotContain(".Composition", type.Namespace ?? "");
        Assert.DoesNotContain(".Shared", type.Namespace ?? "");
    }

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(Path.GetDirectoryName(path)!)!.Parent!.FullName;
}
