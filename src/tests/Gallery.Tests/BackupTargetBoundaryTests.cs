using System.Reflection;
using Gallery.Core.Features.Backups;
using Gallery.Core.Features.Backups.Targets;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class BackupTargetBoundaryTests
{
    [Fact]
    public void BackupAndRestoreWorkflows_DoNotRetainOrPassAzureProviderTypes()
    {
        var types = new[] { typeof(GalleryBackupWriter), typeof(GalleryRestore) }
            .SelectMany(type => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Append(type));
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
            $"Expected Azure SDK types in the backup and restore workflows: none. Actual: [{string.Join(", ", providerTypes)}].");
    }

    [Fact]
    public void InvalidAzureConnectionString_IsRejectedAtGatewayConstructionWithoutEchoingCredentials()
    {
        const string invalidConnection = "invalid connection credential";

        var failure = Assert.Throws<InvalidOperationException>(() =>
            BackupTargetAzure.ValidateDestination(new StoredBackupSettings("azure", "", "container", "", invalidConnection, null, null)));

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
        var failure = Assert.Throws<InvalidOperationException>(() => new BackupTargetAzure(sasUrl));

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
        var target = new BackupTargetLocal(fixture.Root);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => target.DownloadAsync(archiveName, destination));

        AsserterExceptionContract.AssertMessageContains(["filenames", "not paths"], failure.Message);
        Assert.False(File.Exists(destination),
            $"Expected downloaded archive at '{destination}' after rejected traversal: absent. Actual exists: {File.Exists(destination)}.");
    }

    [Fact]
    public async Task LocalTarget_TransfersArchivesWithoutReplacingExistingFiles()
    {
        using var fixture = new TestVault();
        var directory = Path.Combine(fixture.Root, "backups");
        Directory.CreateDirectory(directory);
        var archiveName = "full-20261121T000000000Z-0123456789abcdef0123456789abcdef.zip";
        var source = Path.Combine(fixture.Root, "source.zip");
        var downloaded = Path.Combine(fixture.Root, "downloaded.zip");
        var contents = Enumerable.Range(0, 100_000).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(source, contents);
        var target = new BackupTargetLocal(directory);

        await target.UploadAsync(archiveName, source);
        await target.DownloadAsync(archiveName, downloaded);

        Assert.Equal(contents, await File.ReadAllBytesAsync(downloaded));
        await Assert.ThrowsAsync<IOException>(() => target.UploadAsync(archiveName, source));
        await Assert.ThrowsAsync<IOException>(() => target.DownloadAsync(archiveName, downloaded));
        Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(directory, archiveName)));
    }

    [Fact]
    public async Task LocalTarget_ListingOmitsNamesRejectedByArchiveValidation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new TestVault();
        var directory = Path.Combine(fixture.Root, "backups");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "full.zip"), []);
        await File.WriteAllBytesAsync(Path.Combine(directory, "weekly:copy.zip"), []);

        var names = await new BackupTargetLocal(directory).ListAsync();

        Assert.Equal(["full.zip"], names);
    }
}
