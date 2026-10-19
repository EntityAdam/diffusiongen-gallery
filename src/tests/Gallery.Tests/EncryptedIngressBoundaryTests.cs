using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

// These ingress contracts deliberately combine malformed external envelopes with unusable crypto
// inputs. Boundary-specific context must win before derivation or AES can inspect those inputs.
[Trait("Category", "class_test")]
public sealed class EncryptedIngressBoundaryTests
{
    [Theory]
    [InlineData("KeyCheck", false)]
    [InlineData("KeyCheck", true)]
    [InlineData("Passphrase", false)]
    [InlineData("Passphrase", true)]
    [InlineData("Recovery", false)]
    [InlineData("Recovery", true)]
    public async Task VaultHeader_WhenAnyEnvelopeIsMalformed_RejectsItBeforeDerivingAKey(string member, bool wrongMagic)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var headerPath = Path.Combine(fixture.Root, "vault.json");
        var header = JsonNode.Parse(await File.ReadAllTextAsync(headerPath))!;
        var encoded = Convert.ToBase64String(MalformedEnvelope(wrongMagic));
        if (member == "KeyCheck") header[member] = encoded;
        else header[member]!["WrappedKey"] = encoded;
        await File.WriteAllTextAsync(headerPath, header.ToJsonString());
        var expectedSource = member switch
        {
            "KeyCheck" => "vault key check",
            "Passphrase" => "vault passphrase key slot",
            _ => "vault recovery key slot"
        };

        // A null secret would fail inside derivation if the header had not been rejected first.
        var exception = await Assert.ThrowsAsync<CryptographicException>(() => fixture.Vault.UnlockWithPassphraseAsync(null!));

        AsserterExceptionContract.AssertMessageContains(["Invalid encrypted file format", expectedSource], exception.Message);
    }

    [Theory]
    [InlineData("record", false)]
    [InlineData("record", true)]
    [InlineData("setting", false)]
    [InlineData("setting", true)]
    [InlineData("thumbnail", false)]
    [InlineData("thumbnail", true)]
    [InlineData("verification-thumbnail", false)]
    [InlineData("verification-thumbnail", true)]
    public async Task Catalog_WhenStoredEnvelopeIsMalformed_RejectsItsResultBeforeDecryption(string route, bool wrongMagic)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var imageRecord = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        using var keyLease = fixture.Session.AcquireUnlockedSession().Borrow();
        var envelope = MalformedEnvelope(wrongMagic);
        var sql = route switch
        {
            "record" => "UPDATE images SET record = $payload WHERE id = $id",
            "setting" => "INSERT INTO settings (id, payload) VALUES ($id, $payload)",
            _ => "UPDATE images SET thumbnail = $payload WHERE id = $id"
        };
        var id = route == "setting" ? "ingress-setting" : imageRecord.Id;
        await CorruptStoredValueAsync(fixture.Root, sql, id, envelope);
        Func<Task> read = route switch
        {
            "record" => () => fixture.Store.ReadRecordsAsync([]),
            "setting" => () => fixture.Store.ReadSettingsAsync<UiPreferences>(id, "ingress-setting:v1", []),
            "thumbnail" => () => fixture.Store.ReadThumbnailAsync(id, []),
            _ => () => fixture.Store.ReadVerificationAsync(id, keyLease.Bytes)
        };
        var expectedSource = route switch
        {
            "record" => $"stored value 'record:{id}'",
            "setting" => "stored value 'ingress-setting:v1'",
            "thumbnail" => $"thumbnail '{id}'",
            _ => $"verification thumbnail '{id}'"
        };

        var exception = await Assert.ThrowsAsync<CryptographicException>(read);

        AsserterExceptionContract.AssertMessageContains(["Invalid encrypted file format", expectedSource], exception.Message);
    }

    [Theory]
    [InlineData("reader", false)]
    [InlineData("reader", true)]
    [InlineData("preview", false)]
    [InlineData("preview", true)]
    [InlineData("viewer", false)]
    [InlineData("viewer", true)]
    [InlineData("export", false)]
    [InlineData("export", true)]
    [InlineData("verify", false)]
    [InlineData("verify", true)]
    [InlineData("stream", false)]
    [InlineData("stream", true)]
    public async Task Media_WhenStoredEnvelopeIsMalformed_RejectsItAtAcquisitionForEveryConsumer(string route, bool wrongMagic)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var imageRecord = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await File.WriteAllBytesAsync(imageRecord.StoredPath, MalformedEnvelope(wrongMagic));
        Func<Task> read = route switch
        {
            "reader" => () => StoredMediaProcessor.ReadAsync(imageRecord, []),
            "preview" => () => fixture.Service.PreviewAsync(imageRecord.Id),
            "viewer" => () => fixture.Service.ViewerImageAsync(imageRecord.Id, ImageResolution.Original),
            "export" => () => fixture.Service.ExportAsync(imageRecord.Id),
            "verify" => () => fixture.Service.VerifyImportedAsync(imageRecord.Id),
            _ => () => fixture.Service.OpenMediaAsync(imageRecord.Id)
        };

        var exception = await Assert.ThrowsAsync<CryptographicException>(read);

        AsserterExceptionContract.AssertMessageContains(
            ["Invalid encrypted file format", $"stored media '{imageRecord.Id}'"], exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveManifest_WhenEnvelopeIsMalformed_RejectsItBeforeInspectingTheKey(bool wrongMagic)
    {
        var envelope = MalformedEnvelope(wrongMagic);

        var exception = Assert.Throws<CryptographicException>(() => ArchiveManifestProcessor.Read<Dictionary<string, string>>(envelope, []));

        AsserterExceptionContract.AssertMessageContains(["Invalid encrypted file format", "backup manifest"], exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrivePayload_WhenEnvelopeIsMalformed_RejectsItBeforeInspectingTheKey(bool wrongMagic)
    {
        var driveOperation = new DriveOperation(42, DriveOperationKind.DeleteFile, new byte[32], null, MalformedEnvelope(wrongMagic), 0, 0);

        var exception = Assert.Throws<CryptographicException>(() => DriveOperationProcessor.Read(driveOperation, []));

        AsserterExceptionContract.AssertMessageContains(["Invalid encrypted file format", "drive operation payload '42'"], exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedDrivePayload_WhenEnvelopeIsMalformed_ReportsUnreadableWithoutDeletingMedia(bool wrongMagic)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var imageRecord = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Store.EnqueueDriveOperationsAsync(
            [new NewDriveOperation(DriveOperationKind.DeleteFile, new byte[32], imageRecord.Id, MalformedEnvelope(wrongMagic))]);

        await fixture.Service.LoadAsync();
        await fixture.Service.WaitForDriveOperationsAsync();

        Assert.True(File.Exists(imageRecord.StoredPath));
        Assert.True(fixture.Service.DriveQueueStatus.Failed == 1,
            $"Unreadable persisted queue entries: Expected: 1; Actual: {fixture.Service.DriveQueueStatus.Failed}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedRecovery_WhenEnvelopeIsMalformed_PreservesItAndReportsConflict(bool wrongMagic)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var imageRecord = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var stagedPath = imageRecord.StoredPath + ".removing-" + imageRecord.Id;
        File.Move(imageRecord.StoredPath, stagedPath);
        var envelope = MalformedEnvelope(wrongMagic);
        await File.WriteAllBytesAsync(stagedPath, envelope);

        await fixture.Service.LoadAsync();
        await fixture.Service.WaitForDriveOperationsAsync();

        Assert.False(File.Exists(imageRecord.StoredPath));
        Assert.Equal(envelope, await File.ReadAllBytesAsync(stagedPath));
        Assert.True(fixture.Service.DriveQueueStatus.Failed == 1,
            $"Conflicted recovery entries: Expected: 1; Actual: {fixture.Service.DriveQueueStatus.Failed}.");
    }

    private static byte[] MalformedEnvelope(bool wrongMagic) => wrongMagic ? new byte[36] : [0x44];

    private static async Task CorruptStoredValueAsync(string root, string sql, string id, byte[] payload)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "gallery.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }
}
