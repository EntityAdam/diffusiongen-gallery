using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Queries;
using Gallery.Core.Services;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Processors.Hashing;
using Gallery.Core.Services.Sessions;
using Gallery.Web.Components;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainArchitectureTests
{
    [Fact]
    public void ProductionAssemblies_KeepDomainBehaviorBehindAnIndependentCoreEntry()
    {
        var core = typeof(GalleryService).Assembly;
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
        var core = typeof(GalleryService).Assembly;
        foreach (var method in typeof(GalleryService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
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
        Assert.Same(typeof(GalleryService).Assembly, type.Assembly);
    }

    private static void AssertPublicContract(Type type, Assembly core)
    {
        if (type.HasElementType) AssertPublicContract(type.GetElementType()!, core);
        foreach (var argument in type.GenericTypeArguments) AssertPublicContract(argument, core);
        if (type.Assembly != core) return;
        Assert.True(type.IsPublic || type.IsNestedPublic, $"Public entry exposes internal {type.FullName}.");
        Assert.DoesNotContain(".DataManagers", type.Namespace ?? "");
        Assert.DoesNotContain(".Processors", type.Namespace ?? "");
        Assert.DoesNotContain(".Sessions", type.Namespace ?? "");
        Assert.DoesNotContain(".Gateways", type.Namespace ?? "");
    }

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(Path.GetDirectoryName(path)!)!.Parent!.FullName;
}
