using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Ports.Models;

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
        await fixture.Service.AddTagAsync("old");
        await fixture.Service.AddTagAsync("target");
        await fixture.Service.SaveViewAsync("Multiple tags", new GalleryFilter { Tag = "old, keep, target" });

        await fixture.Service.RenameTagAsync("OLD", "target");
        Assert.Equal("target, keep", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Tag);

        await fixture.Service.DeleteTagAsync("target");
        Assert.Equal("target, keep", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Tag);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task UnusedTags_AreIndependentOfMediaAndSurviveReopening(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        await Task.WhenAll(fixture.Service.AddTagAsync("Cinematic"), fixture.Service.AddTagAsync("Landscapes"));
        await fixture.Service.AddTagAsync("cinematic");
        Assert.Equal(new[] { new TagDefinition("Cinematic", 0), new TagDefinition("Landscapes", 0) }, await fixture.Service.LoadTagCatalogAsync());
        Assert.Empty(await fixture.Service.ListAsync());
        string vault = fixture.Service.VaultRoot;
        await fixture.Service.CloseVaultAsync();
        await fixture.Service.OpenExistingVaultAsync(vault);
        await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(new[] { "Cinematic", "Landscapes" }, await fixture.Service.LoadTagNamesAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Rename_MergesEveryAssignmentAndSavedFilter_DeleteRemovesAssignmentsOnly(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await fixture.Service.UpdateAsync(first.Id, image => image with { Tags = "old, KEEP", IsFavorite = true, Rating = 5 });
        await fixture.Service.UpdateAsync(second.Id, image => image with { Tags = "Old, target", IsMarkedForDeletion = true });
        await fixture.Service.SaveViewAsync("Tagged", new GalleryFilter { Tag = "old" });

        await fixture.Service.RenameTagAsync("OLD", "TARGET");
        Assert.Equal(new TagDefinition("target", 2), Assert.Single(await fixture.Service.LoadTagCatalogAsync(), tag => tag.Name == "target"));
        Assert.DoesNotContain(await fixture.Service.LoadTagNamesAsync(), tag => tag.Equals("old", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("target", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Tag);
        await fixture.Service.RenameTagAsync("target", "Target");
        Assert.Equal(new TagDefinition("Target", 2), Assert.Single(await fixture.Service.LoadTagCatalogAsync(), tag => tag.Name == "Target"));
        await fixture.Service.DeleteTagAsync("TARGET");

        var records = await fixture.Service.ListAsync();
        Assert.Equal(2, records.Count);
        var savedFirst = Assert.Single(records, image => image.Id == first.Id);
        var savedSecond = Assert.Single(records, image => image.Id == second.Id);
        Assert.Equal("KEEP", savedFirst.Tags);
        Assert.True(savedFirst.IsFavorite);
        Assert.Equal(5, savedFirst.Rating);
        Assert.True(savedSecond.IsMarkedForDeletion);
        Assert.Equal("", savedSecond.Tags);
        Assert.Equal(new[] { new TagDefinition("KEEP", 1) }, await fixture.Service.LoadTagCatalogAsync());
        Assert.Equal("Target", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Tag);
    }

    [Fact]
    public async Task LegacyTags_AreAdoptedAndRemainDefinedAfterTheLastAssignmentIsRemoved()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "legacy.png", "folder");
        await fixture.Store.UpdateAsync(image with { Tags = "legacy" }, fixture.Session.Key);
        Assert.Equal(new[] { "legacy" }, await fixture.Service.LoadTagNamesAsync());
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { Tags = "" });
        Assert.Equal(new[] { new TagDefinition("legacy", 0) }, await fixture.Service.LoadTagCatalogAsync());
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
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var other = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "other.png", "folder");
        await fixture.Service.UpdateAsync(first.Id, image => image with { Tags = "Current image" });
        await fixture.Service.UpdateAsync(other.Id, image => image with { Tags = "Elsewhere" });
        await fixture.Service.AddTagAsync("Unused \"quoted\" tag");
        var settings = new VisionSettings { Model = "vision", SystemPrompt = "Base instructions.", TagVocabularyInstructions = "Reuse vocabulary when relevant; allow new tags." };
        await fixture.Service.SaveSettingsAsync(settings);
        var tags = (await fixture.Service.AnalyzeImageAsync(settings, "image")).Tags;
        using var request = JsonDocument.Parse(handler.Body!);
        var messages = request.RootElement.GetProperty("messages");
        var system = messages[0].GetProperty("content").GetString()!;
        Assert.StartsWith(settings.SystemPrompt + " " + settings.TagVocabularyInstructions, system);
        string prompt = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        string payload = prompt.Split("Existing tags: ")[1];
        Assert.Equal(new[] { "Current image", "Elsewhere", "Unused \"quoted\" tag" }, JsonSerializer.Deserialize<string[]>(payload));
        Assert.Equal("Elsewhere, New idea", tags);
        Assert.DoesNotContain("New idea", await fixture.Service.LoadTagNamesAsync());
        Assert.Equal(0, Assert.Single(await fixture.Service.LoadTagCatalogAsync(), tag => tag.Name.StartsWith("Unused")).MediaCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("one,two")]
    [InlineData("one\ntwo")]
    public async Task InvalidTagNames_DoNotChangeTheCatalog(string name)
    {
        using var fixture = new ServiceFixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.AddTagAsync(name));
        Assert.Empty(await fixture.Service.LoadTagCatalogAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task CsvImportAndExport_RoundTripDefinitionsPreserveAssignmentsAndSkipDuplicates(CatalogBackend backend)
    {
        using var fixture = new ServiceFixture(backend);
        await fixture.InitializeAsync();
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await fixture.Service.UpdateAsync(image.Id, edit => edit with { Tags = "Existing", Rating = 4 });
        await fixture.Service.AddTagAsync("Unused");
        await fixture.Service.SaveViewAsync("Existing only", new GalleryFilter { Tag = "Existing" });
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("\uFEFFtag\r\n\"existing\"\r\n\"quoted \"\"tag\"\"\"\r\n\"星空\"\r\nnew\r\nNEW\r\n\r\n"));

        Assert.Equal((3, 2), await fixture.Service.ImportTagsCsvAsync(input));

        var catalog = await fixture.Service.LoadTagCatalogAsync();
        Assert.Equal(new[] { "Existing", "new", "quoted \"tag\"", "Unused", "星空" }, catalog.Select(tag => tag.Name));
        Assert.Equal(1, Assert.Single(catalog, tag => tag.Name == "Existing").MediaCount);
        Assert.All(catalog.Where(tag => tag.Name != "Existing"), tag => Assert.Equal(0, tag.MediaCount));
        var stored = Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal("Existing", stored.Tags);
        Assert.Equal(4, stored.Rating);
        Assert.Equal("Existing", Assert.Single(await fixture.Service.LoadViewsAsync()).GalleryFilter.Tag);
        var file = await fixture.Service.ExportTagsCsvAsync();
        try
        {
            Assert.Equal("tags.csv", file.FileName);
            Assert.StartsWith("text/csv", file.ContentType);
            var bytes = GalleryService.CopyExportContent(file);
            Assert.Equal("\uFEFFtag\r\n\"Existing\"\r\n\"new\"\r\n\"quoted \"\"tag\"\"\"\r\n\"Unused\"\r\n\"星空\"\r\n", Encoding.UTF8.GetString(bytes));
            using var reimport = new MemoryStream(bytes);
            Assert.Equal((0, 5), await fixture.Service.ImportTagsCsvAsync(reimport));
        }
        finally { GalleryService.ClearExportContent(file); }
        var vault = fixture.Service.VaultRoot;
        await fixture.Service.CloseVaultAsync();
        await fixture.Service.OpenExistingVaultAsync(vault);
        await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(catalog, await fixture.Service.LoadTagCatalogAsync());
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
        await fixture.Service.AddTagAsync("Keep");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ImportTagsCsvAsync(input));

        Assert.Equal(new[] { "Keep" }, await fixture.Service.LoadTagNamesAsync());
    }

    [Fact]
    public async Task TagCsv_RejectsInvalidEncodingAndOversizedFilesAndAcceptsAnEmptyCatalog()
    {
        using var fixture = new ServiceFixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        using var invalid = new MemoryStream([.. Encoding.UTF8.GetBytes("tag\n"), 0xff]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ImportTagsCsvAsync(invalid));
        using var oversized = new MemoryStream(new byte[GalleryService.MaxTagCsvBytes + 1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ImportTagsCsvAsync(oversized));
        Assert.Empty(await fixture.Service.LoadTagNamesAsync());
        var file = await fixture.Service.ExportTagsCsvAsync();
        try
        {
            using var input = new MemoryStream(GalleryService.CopyExportContent(file));
            Assert.Equal((0, 0), await fixture.Service.ImportTagsCsvAsync(input));
        }
        finally { GalleryService.ClearExportContent(file); }
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
        public GalleryService Service { get; }
        public ServiceFixture(CatalogBackend backend, HttpClient? client = null) => Service = GalleryService.Create(Path.Combine(root, "vault"),
            new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(root, "state") }, client ?? new HttpClient());
        public Task<string> InitializeAsync() => Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        public void Dispose()
        {
            Service.Dispose();
            Service.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
