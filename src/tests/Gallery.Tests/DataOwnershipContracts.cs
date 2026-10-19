using System.Reflection;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DataOwnershipContracts
{
    [Fact]
    public void QueuedEncryptedValues_WhenSourceBuffersChange_ThenKeepTheirOriginalBytes()
    {
        byte[] target = [1, 2];
        byte[] payload = [3, 4];
        var pending = new NewDriveOperation(DriveOperationKind.DeleteFile, target, "image", payload);
        var queued = new DriveOperation(1, DriveOperationKind.DeleteFile, target, "image", payload, 0, 0);
        var indexed = new IndexedFile(target, 10);

        target[0] = 99;
        payload[0] = 99;

        Assert.Equal(new byte[] { 1, 2 }, pending.Target);
        Assert.Equal(new byte[] { 3, 4 }, pending.Payload);
        Assert.Equal(new byte[] { 1, 2 }, queued.Target);
        Assert.Equal(new byte[] { 3, 4 }, queued.Payload);
        Assert.Equal(new byte[] { 1, 2 }, indexed.Target);
    }

    [Fact]
    public void OwnedThumbnail_WhenDisposed_ThenClearsItsBufferAndRevokesAccess()
    {
        byte[] ownedBytes = [1, 2, 3];
        var thumbnail = new ImageThumbnail(ownedBytes);
        var borrowedContent = thumbnail.Content;

        thumbnail.Dispose();
        thumbnail.Dispose();

        Assert.Equal(new byte[] { 0, 0, 0 }, ownedBytes);
        Assert.Equal(new byte[] { 0, 0, 0 }, borrowedContent.ToArray());
        Assert.Throws<ObjectDisposedException>(() => thumbnail.Content);
    }

    [Fact]
    public async Task Catalog_WhenDeletedAndRecreated_ThenSameStoreEstablishesTheNewSchema()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Store.InitializeAsync();
        using var keyLease = fixture.Session.AcquireUnlockedSession().Borrow();
        File.Delete(Path.Combine(fixture.Root, "gallery.db"));

        await fixture.Store.InitializeAsync();
        var records = await fixture.Store.ReadRecordsAsync(keyLease.Bytes);

        Assert.Empty(records);
    }

    [Fact]
    public void CatalogOperations_WhenInspected_ThenExposeNoDriverTypesOrLiveConnectionState()
    {
        var store = typeof(GalleryStore);
        foreach (var method in store.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            AssertNoProviderType(method.ReturnType);
            foreach (var parameter in method.GetParameters()) AssertNoProviderType(parameter.ParameterType);
        }
        Assert.DoesNotContain(store.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType.Namespace == "Microsoft.Data.Sqlite");
        Assert.Null(store.GetNestedType("ImportCatalog", BindingFlags.NonPublic));
    }

    private static void AssertNoProviderType(Type type)
    {
        Assert.NotEqual("Microsoft.Data.Sqlite", type.Namespace);
        foreach (var argument in type.GenericTypeArguments) AssertNoProviderType(argument);
    }
}
