using System.Reflection;
using Gallery.Core.Services;
using Gallery.Core.Services.Gateways;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BackupTargetBoundaryTests
{
    [Fact]
    public void GalleryEntryManager_DoesNotRetainOrPassAzureProviderTypes()
    {
        var types = typeof(GalleryService).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Append(typeof(GalleryService)).ToArray();
        var exposedTypes = types.SelectMany(type =>
            type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(field => field.FieldType)
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                        .Append(method.ReturnType)
                        .Concat(method.GetMethodBody()?.LocalVariables.Select(local => local.LocalType) ?? []))));

        var providerTypes = exposedTypes.Where(type => type.Namespace?.StartsWith("Azure.", StringComparison.Ordinal) == true)
            .Select(type => type.FullName).Distinct().ToArray();

        Assert.True(providerTypes.Length == 0,
            $"Expected Azure SDK types in the gallery entry manager: none. Actual: [{string.Join(", ", providerTypes)}].");
    }

    [Fact]
    public void InvalidAzureConnectionString_IsRejectedAtGatewayConstructionWithoutEchoingCredentials()
    {
        const string invalidConnection = "invalid connection credential";

        var failure = Assert.Throws<InvalidOperationException>(() =>
            AzureBackupTarget.ValidateConnectionString(invalidConnection));

        Assert.IsType<FormatException>(failure.InnerException);
        AsserterExceptionContract.AssertMessageContains(["Azure", "connection string", "invalid"], failure.Message);
        Assert.DoesNotContain(invalidConnection, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.test/container?sr=c&sp=rl&sig=secret")]
    [InlineData("https://example.test/container/blob?sr=c&sp=rl&sig=secret")]
    [InlineData("https://example.test/container?sr=c&sp=r&sig=secret")]
    public void InvalidContainerSas_IsRejectedBeforeStorageAccess(string sasUrl)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new AzureBackupTarget(sasUrl));

        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.NotEmpty(failure.Message);
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("..\\escape.zip")]
    [InlineData("drive:escape.zip")]
    public async Task LocalArchiveTraversal_IsRejectedBeforeCreatingTheDestination(string archiveName)
    {
        using var fixture = new TestVault();
        var destination = Path.Combine(fixture.Root, "download.zip");
        var target = new LocalBackupTarget(fixture.Root);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => target.DownloadAsync(archiveName, destination));

        AsserterExceptionContract.AssertMessageContains(["filenames", "not paths"], failure.Message);
        Assert.False(File.Exists(destination),
            $"Expected downloaded archive at '{destination}' after rejected traversal: absent. Actual exists: {File.Exists(destination)}.");
    }
}
