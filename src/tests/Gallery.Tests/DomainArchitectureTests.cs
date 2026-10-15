using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Gallery.Core.Models;
using Gallery.Core.Processors;
using Gallery.Core.Queries;
using Gallery.Web.Managers.Gateways;
using Gallery.Web.Models;
using Gallery.Web.Services;
using Gallery.Web.Services.DataManagers;
using Gallery.Web.Services.Processors;
using Gallery.Web.Services.Sessions;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainArchitectureTests
{
    [Fact]
    public void ProductionAssemblies_DependInwardOnAnIndependentCoreLibrary()
    {
        var core = typeof(ImageRecord).Assembly;
        var web = typeof(GalleryService).Assembly;

        Assert.Equal("Gallery.Core", core.GetName().Name);
        Assert.NotSame(web, core);
        Assert.Contains(web.GetReferencedAssemblies(), reference => reference.Name == "Gallery.Core");
        Assert.All(core.GetReferencedAssemblies(), reference =>
            Assert.StartsWith("System", reference.Name!));
        Assert.All(new[] { typeof(DuplicateRules), typeof(LibraryRules), typeof(ReviewDecision),
            typeof(PromptRules), typeof(RemovalRules), typeof(GalleryFilterProcessor) },
            type => Assert.Same(core, type.Assembly));
        Assert.Null(web.GetType(typeof(DuplicateRules).FullName!));
        Assert.All(new[] { typeof(ComfyMetadata), typeof(PerceptualImageHasher), typeof(VisionService),
            typeof(VideoTools), typeof(GalleryStore), typeof(VaultStore), typeof(VaultSession) }, type =>
        {
            Assert.Same(web, type.Assembly);
            Assert.False(type.IsPublic);
        });
    }

    [Fact]
    public void CoreProject_HasNoOuterProjectDependenciesOrLinkedSourceCompilation()
    {
        var sourceRoot = SourceRoot();
        var coreProject = XDocument.Load(Path.Combine(sourceRoot, "Gallery.Core", "Gallery.Core.csproj"));
        var webProject = XDocument.Load(Path.Combine(sourceRoot, "Gallery.Web", "Gallery.Web.csproj"));

        Assert.Equal("Microsoft.NET.Sdk", coreProject.Root!.Attribute("Sdk")!.Value);
        Assert.Empty(coreProject.Descendants("ProjectReference"));
        Assert.Equal("SixLabors.ImageSharp",
            Assert.Single(coreProject.Descendants("PackageReference")).Attribute("Include")!.Value);
        Assert.Empty(coreProject.Descendants("FrameworkReference"));
        Assert.Empty(webProject.Descendants("Compile"));
        Assert.EndsWith("Gallery.Core.csproj",
            Assert.Single(webProject.Descendants("ProjectReference")).Attribute("Include")!.Value);
        var productionProjects = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(sourceRoot, path).StartsWith("tests" + Path.DirectorySeparatorChar))
            .Select(path => Path.GetFileName(path)).Order().ToArray();
        Assert.Equal(["Gallery.Core.csproj", "Gallery.Web.csproj"], productionProjects);
    }

    [Fact]
    public void WebBoundary_ExposesOnlyItsOwnModels()
    {
        var core = typeof(ImageRecord).Assembly;
        var web = typeof(GalleryService).Assembly;
        var domainModels = core.GetExportedTypes().Where(type => type.Namespace == "Gallery.Core.Models");
        foreach (var domainType in domainModels)
        {
            var webType = web.GetType($"Gallery.Web.Models.Web{domainType.Name}");
            Assert.NotNull(webType);
            Assert.NotEqual(domainType, webType);
            Assert.Same(web, webType!.Assembly);
        }
        foreach (var method in typeof(GalleryService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            AssertNoCoreType(method.ReturnType);
            foreach (var parameter in method.GetParameters()) AssertNoCoreType(parameter.ParameterType);
        }
        foreach (var type in web.GetExportedTypes().Where(type => type.Namespace == "Gallery.Web.Models"))
        {
            Assert.StartsWith("Web", type.Name);
            foreach (var property in type.GetProperties()) AssertNoCoreType(property.PropertyType);
        }
    }

    private static void AssertNoCoreType(Type type)
    {
        Assert.NotSame(typeof(ImageRecord).Assembly, type.Assembly);
        if (type.HasElementType) AssertNoCoreType(type.GetElementType()!);
        foreach (var argument in type.GenericTypeArguments) AssertNoCoreType(argument);
    }

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(Path.GetDirectoryName(path)!)!.Parent!.FullName;
}
