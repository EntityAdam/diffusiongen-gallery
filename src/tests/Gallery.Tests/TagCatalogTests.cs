using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Gallery.Core.Features.Tags;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class TagCatalogTests
{
    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Rename_PreservesOtherSelectedTagsAndDeduplicatesMergedSavedFilters(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        await fixture.Scope.Tags.AddTagAsync("old");
        await fixture.Scope.Tags.AddTagAsync("target");
        await fixture.Scope.Settings.SaveViewAsync("Multiple tags", new GalleryFilter { Tag = "old, keep, target" });

        await fixture.Scope.Tags.RenameTagAsync("OLD", "target");
        Assert.Equal("target, keep", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Tag);

        await fixture.Scope.Tags.DeleteTagAsync("target");
        Assert.Equal("target, keep", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Tag);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task UnusedTags_AreIndependentOfMediaAndSurviveReopening(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        await Task.WhenAll(fixture.Scope.Tags.AddTagAsync("Cinematic"), fixture.Scope.Tags.AddTagAsync("Landscapes"));
        await fixture.Scope.Tags.AddTagAsync("cinematic");
        Assert.Equal(new[] { new TagDefinition("Cinematic", 0), new TagDefinition("Landscapes", 0) }, await fixture.Scope.Tags.LoadTagCatalogAsync());
        Assert.Empty(await fixture.Scope.Catalog.ListAsync());
        string vault = fixture.Scope.Vault.VaultRoot;
        await fixture.Scope.Vault.CloseVaultAsync();
        await fixture.Scope.Vault.OpenExistingVaultAsync(vault);
        await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(new[] { "Cinematic", "Landscapes" }, await fixture.Scope.Tags.LoadTagNamesAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Rename_MergesEveryAssignmentAndSavedFilter_DeleteRemovesAssignmentsOnly(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await fixture.Scope.Records.UpdateAsync(first.Id, image => image with { Tags = "old, KEEP", IsFavorite = true, Rating = 5 });
        await fixture.Scope.Records.UpdateAsync(second.Id, image => image with { Tags = "Old, target", IsMarkedForDeletion = true });
        await fixture.Scope.Settings.SaveViewAsync("Tagged", new GalleryFilter { Tag = "old" });

        await fixture.Scope.Tags.RenameTagAsync("OLD", "TARGET");
        Assert.Equal(new TagDefinition("target", 2), Assert.Single(await fixture.Scope.Tags.LoadTagCatalogAsync(), tag => tag.Name == "target"));
        Assert.DoesNotContain(await fixture.Scope.Tags.LoadTagNamesAsync(), tag => tag.Equals("old", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("target", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Tag);
        await fixture.Scope.Tags.RenameTagAsync("target", "Target");
        Assert.Equal(new TagDefinition("Target", 2), Assert.Single(await fixture.Scope.Tags.LoadTagCatalogAsync(), tag => tag.Name == "Target"));
        await fixture.Scope.Tags.DeleteTagAsync("TARGET");

        var records = await fixture.Scope.Catalog.ListAsync();
        Assert.Equal(2, records.Count);
        var savedFirst = Assert.Single(records, image => image.Id == first.Id);
        var savedSecond = Assert.Single(records, image => image.Id == second.Id);
        Assert.Equal("KEEP", savedFirst.Tags);
        Assert.True(savedFirst.IsFavorite);
        Assert.Equal(5, savedFirst.Rating);
        Assert.True(savedSecond.IsMarkedForDeletion);
        Assert.Equal("", savedSecond.Tags);
        Assert.Equal(new[] { new TagDefinition("KEEP", 1) }, await fixture.Scope.Tags.LoadTagCatalogAsync());
        Assert.Equal("Target", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Tag);
    }

    [Fact]
    public async Task LegacyTags_AreAdoptedAndRemainDefinedAfterTheLastAssignmentIsRemoved()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "legacy.png", "folder");
        await fixture.Store.UpdateAsync(image with { Tags = "legacy" }, fixture.Session.Key);
        Assert.Equal(new[] { "legacy" }, await fixture.Scope.Tags.LoadTagNamesAsync());
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "" });
        Assert.Equal(new[] { new TagDefinition("legacy", 0) }, await fixture.Scope.Tags.LoadTagCatalogAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task RecordEditorRejectsNewTagNamesOutsideTheSharedContract(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Records.UpdateAsync(image.Id, current => current with { Tags = new string('x', 101) }));

        Assert.Equal("", Assert.Single(await fixture.Scope.Catalog.ListAsync()).Tags);
        Assert.Empty(await fixture.Scope.Tags.LoadTagNamesAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite, false)]
    [InlineData(CatalogBackend.Sqlite, true)]
    [InlineData(CatalogBackend.Turso, false)]
    [InlineData(CatalogBackend.Turso, true)]
    public async Task InvalidCsvDoesNotInitializeCatalogOrChangeRecordsAndViews(CatalogBackend backend, bool defaultCatalog)
    {
        using var fixture = new SnapshotFixture(backend);
        await fixture.InitializeAsync(defaultCatalog);
        var image = await fixture.AddRecordAsync();
        await fixture.Store.SaveViewAsync("Saved", new GalleryFilter { Tag = "legacy" }, fixture.Key);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("tag\nvalid\n\"invalid,tag\"\n"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Tags.ImportTagsCsvAsync(input));

        Assert.Contains("CSV tag 2:", exception.Message);
        await fixture.AssertUninitializedAsync(defaultCatalog);
        Assert.Equal("legacy", (await fixture.Store.ReadRecordAsync(image.Id, fixture.Key)).Tags);
        Assert.Equal("legacy", Assert.Single(await fixture.Store.LoadViewsAsync(fixture.Key)).GalleryFilter.Tag);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite, false)]
    [InlineData(CatalogBackend.Sqlite, true)]
    [InlineData(CatalogBackend.Turso, false)]
    [InlineData(CatalogBackend.Turso, true)]
    public async Task InvalidEditDoesNotInitializeCatalogOrChangeRecord(CatalogBackend backend, bool defaultCatalog)
    {
        using var fixture = new SnapshotFixture(backend);
        await fixture.InitializeAsync(defaultCatalog);
        var image = await fixture.AddRecordAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = new string('x', 101) }));

        Assert.Contains("1–100 characters", exception.Message);
        await fixture.AssertUninitializedAsync(defaultCatalog);
        Assert.Equal("legacy", (await fixture.Store.ReadRecordAsync(image.Id, fixture.Key)).Tags);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task EmptyCsvInitializesMissingCatalog(CatalogBackend backend)
    {
        using var fixture = new SnapshotFixture(backend);
        await fixture.InitializeAsync(false);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("tag\n"));

        Assert.Equal((0, 0), await fixture.Scope.Tags.ImportTagsCsvAsync(input));

        var catalog = await fixture.Store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, fixture.Key);
        Assert.NotNull(catalog);
        Assert.False(catalog.Names.IsDefault);
        Assert.Empty(catalog.Names);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task CsvImportReconcilesUninitializedCatalogAndCommitsNewNames(CatalogBackend backend)
    {
        using var fixture = new SnapshotFixture(backend);
        await fixture.InitializeAsync(false);
        await fixture.AddRecordAsync();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("tag\nlegacy\nnew\n"));

        Assert.Equal((1, 1), await fixture.Scope.Tags.ImportTagsCsvAsync(input));

        var catalog = await fixture.Store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, fixture.Key);
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "legacy", "new" }, catalog.Names);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task ValidEditCommitsRecordAndReconciledUninitializedCatalog(CatalogBackend backend)
    {
        using var fixture = new SnapshotFixture(backend);
        await fixture.InitializeAsync(false);
        var image = await fixture.AddRecordAsync();

        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "legacy, new" });

        Assert.Equal("legacy, new", (await fixture.Store.ReadRecordAsync(image.Id, fixture.Key)).Tags);
        var catalog = await fixture.Store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, fixture.Key);
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "legacy", "new" }, catalog.Names);
    }

    [Fact]
    public async Task PreviouslyStoredLegacyTagNamesRemainTransferable()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "legacy.png", "folder");
        var legacyName = new string('x', 101);
        await fixture.Store.UpdateAsync(image with { Tags = legacyName }, fixture.Session.Key);
        await fixture.Scope.Records.UpdateAsync(image.Id, current => current with { IsFavorite = true });

        var file = await fixture.Scope.Tags.ExportTagsCsvAsync();
        try
        {
            using var input = new MemoryStream(file.CopyContentBytes());
            Assert.Equal((0, 1), await fixture.Scope.Tags.ImportTagsCsvAsync(input));
        }
        finally { file.ClearContent(); }

        Assert.Equal(new[] { new TagDefinition(legacyName, 1) }, await fixture.Scope.Tags.LoadTagCatalogAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task TagTransaction_RollsBackAssignmentsCatalogAndFiltersWhenAnyRecordFails(CatalogBackend backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "tag-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            IGalleryStore store = new GalleryStoreFactory().Create(root, backend);
            await store.InitializeAsync(key);
            await using var operation = await store.OpenOperationAsync(key);
            var image = new ImageRecord { Id = "existing", Name = "sample", Tags = "old", Sha256 = new string('A', 64) };
            using var thumbnail = new ImageThumbnail([1, 2, 3]);
            await store.InsertAsync(image, thumbnail, key);
            var original = new TagCatalog(["old", "unused"]);
            SavedView[] views = [new("Tagged", new GalleryFilter { Tag = "old" })];
            await store.CommitTagCatalogAsync([], original, views, key);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitTagCatalogAsync(
                [image with { Tags = "new" }, image with { Id = "missing", Tags = "new" }], new(["new"]),
                [new("Tagged", new GalleryFilter { Tag = "new" })], key));
            Assert.Equal("old", (await store.ReadRecordAsync(image.Id, key)).Tags);
            Assert.Equal(original.Names.ToArray(), (await store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, key))!.Names.ToArray());
            Assert.Equal("old", Assert.Single(await store.LoadViewsAsync(key)).GalleryFilter.Tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task VisionReceivesAllRegisteredTagsIncludingUnusedAndOtherMediaTags()
    {
        using var handler = new CaptureHandler();
        using var fixture = new ServiceFixture(CatalogBackend.Sqlite, new HttpClient(handler));
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var other = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "other.png", "folder");
        await fixture.Scope.Records.UpdateAsync(first.Id, image => image with { Tags = "Current image" });
        await fixture.Scope.Records.UpdateAsync(other.Id, image => image with { Tags = "Elsewhere" });
        await fixture.Scope.Tags.AddTagAsync("Unused \"quoted\" tag");
        var settings = new VisionSettings { Model = "vision", SystemPrompt = "Base instructions.", TagVocabularyInstructions = "Reuse vocabulary when relevant; allow new tags." };
        await fixture.Scope.Vision.SaveSettingsAsync(settings);
        var tags = (await fixture.Scope.Vision.AnalyzeImageAsync(settings, "image")).Tags;
        using var request = JsonDocument.Parse(handler.Body!);
        var messages = request.RootElement.GetProperty("messages");
        var system = messages[0].GetProperty("content").GetString()!;
        Assert.StartsWith(settings.SystemPrompt + " " + settings.TagVocabularyInstructions, system);
        string prompt = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        string payload = prompt.Split("Existing tags: ")[1];
        Assert.Equal(new[] { "Current image", "Elsewhere", "Unused \"quoted\" tag" }, JsonSerializer.Deserialize<string[]>(payload));
        Assert.Equal("Elsewhere, New idea", tags);
        Assert.DoesNotContain("New idea", await fixture.Scope.Tags.LoadTagNamesAsync());
        Assert.Equal(0, Assert.Single(await fixture.Scope.Tags.LoadTagCatalogAsync(), tag => tag.Name.StartsWith("Unused")).MediaCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("one,two")]
    [InlineData("one\ntwo")]
    public async Task InvalidTagNames_DoNotChangeTheCatalog(string name)
    {
        using var fixture = new ServiceFixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Tags.AddTagAsync(name));
        Assert.Empty(await fixture.Scope.Tags.LoadTagCatalogAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task CsvImportAndExport_RoundTripDefinitionsPreserveAssignmentsAndSkipDuplicates(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "Existing", Rating = 4 });
        await fixture.Scope.Tags.AddTagAsync("Unused");
        await fixture.Scope.Settings.SaveViewAsync("Existing only", new GalleryFilter { Tag = "Existing" });
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("\uFEFFtag\r\n\"existing\"\r\n\"quoted \"\"tag\"\"\"\r\n\"星空\"\r\nnew\r\nNEW\r\n\r\n"));

        Assert.Equal((3, 2), await fixture.Scope.Tags.ImportTagsCsvAsync(input));

        var catalog = await fixture.Scope.Tags.LoadTagCatalogAsync();
        Assert.Equal(new[] { "Existing", "new", "quoted \"tag\"", "Unused", "星空" }, catalog.Select(tag => tag.Name));
        Assert.Equal(1, Assert.Single(catalog, tag => tag.Name == "Existing").MediaCount);
        Assert.All(catalog.Where(tag => tag.Name != "Existing"), tag => Assert.Equal(0, tag.MediaCount));
        var stored = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Equal("Existing", stored.Tags);
        Assert.Equal(4, stored.Rating);
        Assert.Equal("Existing", Assert.Single(await fixture.Scope.Settings.LoadViewsAsync()).GalleryFilter.Tag);
        var file = await fixture.Scope.Tags.ExportTagsCsvAsync();
        try
        {
            Assert.Equal("tags.csv", file.FileName);
            Assert.StartsWith("text/csv", file.ContentType);
            var bytes = file.CopyContentBytes();
            Assert.Equal("\uFEFFtag\r\n\"Existing\"\r\n\"new\"\r\n\"quoted \"\"tag\"\"\"\r\n\"Unused\"\r\n\"星空\"\r\n", Encoding.UTF8.GetString(bytes));
            using var reimport = new MemoryStream(bytes);
            Assert.Equal((0, 5), await fixture.Scope.Tags.ImportTagsCsvAsync(reimport));
        }
        finally { file.ClearContent(); }
        var vault = fixture.Scope.Vault.VaultRoot;
        await fixture.Scope.Vault.CloseVaultAsync();
        await fixture.Scope.Vault.OpenExistingVaultAsync(vault);
        await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(catalog, await fixture.Scope.Tags.LoadTagCatalogAsync());
    }

    [Theory]
    [InlineData("name\nvalid\n")]
    [InlineData("tag,count\nvalid,1\n")]
    [InlineData("tag\nvalid\nwrong,columns\n")]
    [InlineData("tag\nvalid\n\"unclosed\n")]
    [InlineData("tag\nvalid\n\"comma,tag\"\n")]
    [InlineData("tag\nvalid\n\"line\nbreak\"\n")]
    [InlineData("tag\nvalid\n\"\"\n")]
    [InlineData("tag\nvalid\n\"bad\"quote\n")]
    [InlineData("")]
    public async Task InvalidTagCsv_DoesNotImportAnyRows(string csv)
    {
        using var fixture = new ServiceFixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        await fixture.Scope.Tags.AddTagAsync("Keep");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Tags.ImportTagsCsvAsync(input));

        Assert.Equal(new[] { "Keep" }, await fixture.Scope.Tags.LoadTagNamesAsync());
    }

    [Fact]
    public async Task TagCsv_RejectsInvalidEncodingAndOversizedFilesAndAcceptsAnEmptyCatalog()
    {
        using var fixture = new ServiceFixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        using var invalid = new MemoryStream([.. Encoding.UTF8.GetBytes("tag\n"), 0xff]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Tags.ImportTagsCsvAsync(invalid));
        using var oversized = new MemoryStream(new byte[IGalleryTags.MaxCsvBytes + 1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Tags.ImportTagsCsvAsync(oversized));
        Assert.Empty(await fixture.Scope.Tags.LoadTagNamesAsync());
        var file = await fixture.Scope.Tags.ExportTagsCsvAsync();
        try
        {
            using var input = new MemoryStream(file.CopyContentBytes());
            Assert.Equal((0, 0), await fixture.Scope.Tags.ImportTagsCsvAsync(input));
        }
        finally { file.ClearContent(); }
    }

    private sealed class SnapshotFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tag-snapshot-" + Guid.NewGuid().ToString("N"));
        private readonly VaultSession session = new();
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public IGalleryStore Store { get; }
        public GalleryScope Scope { get; }

        public SnapshotFixture(CatalogBackend backend)
        {
            Directory.CreateDirectory(root);
            Store = new GalleryStoreFactory().Create(root, backend);
            Scope = new GalleryScope(Store, new VaultStore(root), session);
        }

        public async Task InitializeAsync(bool defaultCatalog)
        {
            await Store.InitializeAsync(Key);
            session.Unlock(Key);
            if (defaultCatalog)
                await Store.SaveSettingsAsync(new { }, TagCatalog.SettingId, TagCatalog.SettingPurpose, Key);
        }

        public async Task<ImageRecord> AddRecordAsync()
        {
            var image = new ImageRecord { Id = "existing", Name = "sample", Tags = "legacy", Sha256 = new string('A', 64) };
            using var thumbnail = new ImageThumbnail([1, 2, 3]);
            await Store.InsertAsync(image, thumbnail, Key);
            return image;
        }

        public async Task AssertUninitializedAsync(bool defaultCatalog)
        {
            var catalog = await Store.ReadSettingsAsync<TagCatalog>(TagCatalog.SettingId, TagCatalog.SettingPurpose, Key);
            if (defaultCatalog)
            {
                Assert.NotNull(catalog);
                Assert.True(catalog.Names.IsDefault);
            }
            else Assert.Null(catalog);
        }

        public void Dispose()
        {
            Scope.Dispose();
            Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            CryptographicOperations.ZeroMemory(Key);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            var content = JsonSerializer.Serialize(new { description = "A landscape.", tags = new[] { "elsewhere", "New idea" } });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } })) };
        }
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string root = Path.Combine(TemporaryRoot(), "tag-library-" + Guid.NewGuid().ToString("N"));
        private static string TemporaryRoot()
        {
            string path = Path.GetTempPath();
            return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
        }
        public GalleryScope Scope { get; }
        public ServiceFixture(CatalogBackend backend, HttpClient? client = null) => Scope = GalleryScope.Create(Path.Combine(root, "vault"),
            new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(root, "state") }, client ?? new HttpClient());
        public Task<string> InitializeAsync() => Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        public void Dispose()
        {
            Scope.Dispose();
            Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
