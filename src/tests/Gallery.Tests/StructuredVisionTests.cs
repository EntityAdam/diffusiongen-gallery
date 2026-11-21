using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Infrastructure.Persistence.Sqlite.DataManagers;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

public sealed class StructuredVisionTests
{
    private const string Document = """
        {"scene":{"description":"A man wearing a red hat stands beside a woman wearing a mustard-yellow hat.","booru_tags":["never assign me"]},
         "entities":[
           {"id":"m","kind":"man","description":"A man","attributes":[]},
           {"id":"w","kind":"woman","description":"A woman","attributes":[]},
           {"id":"r","kind":"hat","description":"Red hat","attributes":[{"name":"color","value":"red"}]},
           {"id":"y","kind":"hat","description":"Yellow hat","attributes":[{"name":"color","value":"mustard-yellow"}]}],
         "relationships":[{"subjectId":"m","predicate":"wearing","objectId":"r"},{"subjectId":"w","predicate":"wearing","objectId":"y"}],
         "tags":["PORTRAIT"],"suggested_tags":["new idea"],"future_field":{"preserve":true},"visual":{"medium":"anime"}}
        """;

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"scene\":{},\"entities\":{}}")]
    [InlineData("{\"scene\":[],\"entities\":[]}")]
    [InlineData("{\"scene\":{},\"entities\":[],\"relationships\":null}")]
    [InlineData("[]")]
    public void InvalidTopLevel_IsRejected(string content)
    {
        using var json = JsonDocument.Parse(content);
        Assert.Throws<InvalidOperationException>(() => StructuredVision.Parse(json.RootElement));
    }

    [Theory]
    [InlineData(9997, false)]
    [InlineData(9998, true)]
    [InlineData(9999, false)]
    [InlineData(10000, false)]
    public void Parse_WhenDescriptionCrossesLimit_ThenPreservesSurrogatePairsAndOriginalJson(int prefixLength, bool includesEmoji)
    {
        var description = new string('a', prefixLength) + "\U0001F600" + "b";
        var content = JsonSerializer.Serialize(new { scene = new { description }, entities = Array.Empty<object>(), tags = Array.Empty<string>(), suggested_tags = Array.Empty<string>() });
        using var json = JsonDocument.Parse(content);
        var expectedDescription = new string('a', Math.Min(prefixLength, 10_000)) + (includesEmoji ? "\U0001F600" : "");
        if (description.Length <= 10_000) expectedDescription = description;
        var expectedWarning = description.Length > 10_000;

        var result = StructuredVision.Parse(json.RootElement);

        Assert.Equal(expectedDescription, result.Description);
        Assert.Equal(content, result.Analysis!.Json);
        Assert.Equal(expectedWarning, result.Warning.Contains("Overview shortened", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedProblems_AreRetainedWithWarningsAndCannotProduceFalseRelations()
    {
        const string content = """
            {"scene":{},"entities":[false,{"id":"duplicate"},{"id":"duplicate"},{"id":"good","attributes":[null]}],
             "relationships":[null,{"subjectId":"duplicate","predicate":"holding","objectId":"good"},
             {"subjectId":"good.hand","predicate":"holding","objectId":"good"}],"tags":false,"future":42}
            """;
        using var json = JsonDocument.Parse(content);
        var result = StructuredVision.Parse(json.RootElement);
        Assert.Equal(content, result.Analysis!.Json);
        Assert.NotEmpty(result.Analysis.Warnings);
        Assert.Empty(result.Analysis.Facts.Relationships);
        Assert.Equal("good", Assert.Single(result.Analysis.Facts.Entities).Id);
        Assert.Empty(result.Description);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void Parse_BoundsWarningsForRepeatedMalformedAttributesAndLongEntityIds()
    {
        var id = new string('x', 200_000);
        object?[] attributes = [.. Enumerable.Repeat<object?>(null, 5_000), new { name = "color", value = "blue" }];
        var entities = new List<object> { new { id, kind = "object", description = "A blue object.", attributes } };
        entities.AddRange(Enumerable.Range(0, 100).Select(index => (object)new { id = $"extra-{index}", kind = "", description = "" }));
        var content = JsonSerializer.Serialize(new
        {
            scene = new { description = "A scene." },
            entities,
            relationships = Array.Empty<object>(),
            tags = Array.Empty<string>(),
            suggested_tags = Array.Empty<string>()
        });
        using var json = JsonDocument.Parse(content);

        var result = StructuredVision.Parse(json.RootElement);

        Assert.Equal(content, result.Analysis!.Json);
        Assert.True(result.Analysis.Warnings.Length <= 100);
        Assert.All(result.Analysis.Warnings, warning => Assert.True(warning.Length <= 512));
        Assert.Single(result.Analysis.Warnings, warning => warning.Contains("unusable attribute", StringComparison.Ordinal));
        Assert.Contains("…", Assert.Single(result.Analysis.Warnings, warning => warning.Contains("unusable attribute", StringComparison.Ordinal)));
        Assert.Contains("Additional structured analysis warnings omitted.", result.Analysis.Warnings);
        var analyzedEntity = Assert.Single(result.Analysis.Facts.Entities, entity => entity.Id == id);
        Assert.Equal("blue", Assert.Single(analyzedEntity.Attributes).Value);
    }

    [Fact]
    public void Parse_WhenRepeatedWarningsPrecedeDistinctWarnings_ThenPreservesDistinctDiagnostics()
    {
        var content = JsonSerializer.Serialize(new
        {
            scene = new { description = "A scene." },
            entities = Array.Empty<object>(),
            relationships = Enumerable.Repeat<object?>(null, 150)
        });
        using var json = JsonDocument.Parse(content);

        var warnings = StructuredVision.Parse(json.RootElement).Analysis!.Warnings;

        Assert.Equal(3, warnings.Length);
        Assert.Single(warnings, warning => warning.Contains("relationship", StringComparison.Ordinal));
        Assert.Contains("'tags' is missing; no gallery tags were taken from it.", warnings);
        Assert.Contains("'suggested_tags' is missing; no gallery tags were taken from it.", warnings);
        Assert.DoesNotContain("Additional structured analysis warnings omitted.", warnings);
    }

    [Fact]
    public void Parse_WhenDiagnosticIdCrossesSurrogateBoundary_ThenPreservesValidUnicode()
    {
        var id = new string('x', 79) + "\U0001F600" + "tail";
        var content = JsonSerializer.Serialize(new
        {
            scene = new { description = "A scene." },
            entities = new[] { new { id, kind = "", description = "", attributes = Array.Empty<object>() } },
            tags = Array.Empty<string>(),
            suggested_tags = Array.Empty<string>()
        });
        using var json = JsonDocument.Parse(content);

        var warning = Assert.Single(StructuredVision.Parse(json.RootElement).Analysis!.Warnings);

        Assert.DoesNotContain('\ud83d', warning);
        Assert.Contains(new string('x', 79) + "…", warning);
    }

    [Theory]
    [InlineData("{}", "'tags' is missing", "'suggested_tags' is missing")]
    [InlineData("{\"tags\":false,\"suggested_tags\":null}", "'tags' is not an array", "'suggested_tags' is not an array")]
    public void MissingOrMalformedGalleryTags_PreserveSceneAndBooruMetadataWithWarnings(
        string fields, string tagsWarning, string suggestionsWarning)
    {
        using var tagFields = JsonDocument.Parse(fields);
        var response = new Dictionary<string, object?>
        {
            ["scene"] = new { description = "A sunlit scene.", booru_tags = new[] { "sunlight" } },
            ["entities"] = Array.Empty<object>()
        };
        foreach (var field in tagFields.RootElement.EnumerateObject()) response[field.Name] = field.Value;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));

        var result = StructuredVision.Parse(document.RootElement);

        Assert.Equal("A sunlit scene.", result.Description);
        Assert.Empty(result.Tags);
        Assert.Contains(tagsWarning, result.Warning);
        Assert.Contains(suggestionsWarning, result.Warning);
        Assert.Equal(document.RootElement.GetRawText(), result.Analysis!.Json);
    }

    [Fact]
    public async Task FirstPassWithCustomPromptAndEmptyCatalog_RequestsBothGalleryTagArrays()
    {
        using var fixture = new Fixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        var image = await fixture.ImportAsync();
        var settings = Settings() with { SystemPrompt = "Describe the scene and its entities as JSON." };

        var result = await fixture.Scope.Vision.AnalyzeForDraftAsync(image.Id, settings);

        Assert.Equal(1, fixture.Handler.RequestCount);
        using var request = JsonDocument.Parse(fixture.Handler.Request!);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Equal(settings.SystemPrompt, messages[0].GetProperty("content").GetString());
        var prompt = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("top-level tags and suggested_tags arrays", prompt);
        Assert.Contains("Return tags: []", prompt);
        Assert.Contains("propose useful new gallery tags", prompt);
        Assert.Contains("new idea", result.Tags);
        Assert.DoesNotContain("never assign me", result.Tags);
    }

    [Fact]
    public void PositiveSearch_BindsColorToTheSameHatAndRelationshipToTheSamePerson()
    {
        using var json = JsonDocument.Parse(Document);
        var facts = StructuredVision.Parse(json.RootElement).Analysis!.Facts;
        var query = new VisionQuery { Subject = "man", Relationship = "wearing", Object = "hat", Attribute = "color", Value = "yellow" };
        Assert.False(StructuredVision.Matches(facts, "", query));
        Assert.True(StructuredVision.Matches(facts, "", query with { Subject = "woman" }));
        Assert.False(StructuredVision.Matches(facts, "", query with { Subject = "woman", Relationship = "holding" }));
        Assert.False(StructuredVision.Matches(facts, "changed-image", query with { Subject = "woman" }));
        Assert.True(StructuredVision.Matches(facts, "", new VisionQuery { Subject = "hat", Attribute = "color", Value = "red" }));
    }

    [Theory]
    [InlineData(true, true, "Portrait, new idea")]
    [InlineData(true, false, "Portrait")]
    [InlineData(false, true, "new idea")]
    [InlineData(false, false, "")]
    public async Task TagPolicy_IsIndependentOfModelAdherenceAndCatalogIsOmittedWhenDisabled(bool existing, bool novel, string expected)
    {
        using var fixture = new Fixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        await fixture.Scope.Tags.AddTagAsync("Portrait"); // unused definitions must still be candidates
        var image = await fixture.ImportAsync();
        var settings = Settings() with { UseExistingTags = existing, SuggestNewTags = novel };
        var result = await fixture.Scope.Vision.AnalyzeForDraftAsync(image.Id, settings);
        Assert.Equal(expected, result.Tags);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Empty(Assert.Single(await fixture.Scope.Catalog.ListAsync()).Tags);
        Assert.NotNull(result.Analysis);
        using var request = JsonDocument.Parse(fixture.Handler.Request!);
        var userPrompt = request.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Equal(existing, userPrompt.Contains("Portrait", StringComparison.Ordinal));
        Assert.Contains("top-level tags and suggested_tags arrays", userPrompt);
        Assert.Contains("scene.booru_tags as descriptive metadata only", userPrompt);
        Assert.Contains(existing ? "select only relevant exact names" : "Return tags: []", userPrompt);
        Assert.Contains(novel ? "propose useful new gallery tags" : "Return suggested_tags: []", userPrompt);
        using var original = JsonDocument.Parse(result.Analysis!.Json);
        Assert.Equal("never assign me", original.RootElement.GetProperty("scene").GetProperty("booru_tags")[0].GetString());
        Assert.DoesNotContain("never assign me", result.Tags);
        Assert.Equal(settings.BuildImageAnalysisSystemPrompt(existing), result.Analysis!.EffectiveSystemPrompt);
        await fixture.Scope.Vision.ApproveVisionAsync(image.Id, result.Description, result.Tags, settings.Model, result.Analysis,
            useExistingTags: existing, suggestNewTags: novel);
        Assert.Equal(expected, Assert.Single(await fixture.Scope.Catalog.ListAsync()).Tags);
        Assert.DoesNotContain("never assign me", await fixture.Scope.Tags.LoadTagNamesAsync());
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task AcceptedAnalysis_RoundTripsSeparatelyBacksUpAndDeletesWithImage(CatalogBackend backend)
    {
        using var fixture = new Fixture(backend);
        var recovery = await fixture.InitializeAsync();
        var image = await fixture.ImportAsync();
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { UserDescription = "Manual notes", Tags = "keep", Rating = 3 });
        var result = await fixture.Scope.Vision.AnalyzeForDraftAsync(image.Id, Settings());
        var checkpoint = await fixture.Scope.Vision.OpenVisionCheckpointAsync();
        var draft = new VisionDraft(image.Id, image.Name, result.Description, result.Tags, "vision", Analysis: result.Analysis);
        await checkpoint.StageAsync(draft);
        Assert.Equal(result.Analysis!.Json, Assert.Single((await fixture.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts).Analysis!.Json);
        var approved = await checkpoint.ApproveDraftAsync(draft);
        Assert.NotNull(approved);
        Assert.Equal("Manual notes", approved.UserDescription);
        Assert.Equal(3, approved.Rating);
        Assert.Contains("keep", approved.Tags);
        Assert.True(approved.IsReviewed);
        Assert.NotNull(approved.VisionFacts);
        Assert.DoesNotContain("future_field", JsonSerializer.Serialize(approved)); // raw JSON is not in browse records
        Assert.Contains("future_field", (await fixture.Scope.Vision.ReadVisionAnalysisAsync(image.Id))!.Json);
        await fixture.Scope.Settings.SaveViewAsync("Yellow hat", new GalleryFilter { Vision = new VisionQuery { Subject = "woman", Relationship = "wearing", Object = "hat", Attribute = "color", Value = "yellow" } });
        var backupDir = Path.Combine(fixture.Root, "backups");
        await fixture.Scope.Backup.SaveBackupOptionsAsync("local", backupDir, "", "", "");
        var backup = await fixture.Scope.Backup.CreateFullBackupAsync();
        await fixture.Scope.Vault.CloseVaultAsync();
        await fixture.Scope.Vault.OpenExistingVaultAsync(fixture.VaultPath);
        await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Empty((await fixture.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts);
        Assert.Equal(result.Analysis.Json, (await fixture.Scope.Vision.ReadVisionAnalysisAsync(image.Id))!.Json);
        using var exported = new ExportOwner(await fixture.Scope.Vision.ExportVisionAnalysisAsync(image.Id));
        Assert.Equal(result.Analysis.Json, Encoding.UTF8.GetString(exported.File.CopyContentBytes()));
        var restored = Path.Combine(fixture.Root, "restored");
        await fixture.Scope.Restore.RestoreVaultAsync("local", backupDir, "", "", "", backup.BackupName, restored, recovery, useRecovery: true);
        await fixture.Scope.Vault.CloseVaultAsync();
        await fixture.Scope.Vault.OpenExistingVaultAsync(restored);
        await fixture.Scope.Vault.UnlockWithRecoveryPhraseAsync(recovery);
        Assert.Equal(result.Analysis.Json, (await fixture.Scope.Vision.ReadVisionAnalysisAsync(image.Id))!.Json);
        var records = await fixture.Scope.Catalog.ListAsync();
        Assert.Single(fixture.Scope.Catalog.Filter(records, new GalleryFilter { Vision = new VisionQuery { Subject = "woman", Relationship = "wearing", Object = "hat", Attribute = "color", Value = "yellow" } }));
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { IsMarkedForDeletion = true });
        var removal = await fixture.Scope.MediaMaintenance.PrepareRemovalAsync([image.Id]);
        await fixture.Scope.MediaMaintenance.RemoveAsync(removal, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "");
        Assert.Empty(await fixture.Scope.Catalog.ListAsync());
        var key = await new VaultStore(restored).UnlockWithRecoveryPhraseAsync(recovery);
        try { Assert.Null(await fixture.Store(restored).ReadVisionAnalysisAsync(image.Id, key)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Approval_WhenCheckpointWriteFails_RollsBackAndCanBeRetriedAfterReopen(CatalogBackend backend)
    {
        using var fixture = new Fixture(backend);
        await fixture.InitializeAsync();
        var image = await fixture.ImportAsync();
        var result = await fixture.Scope.Vision.AnalyzeForDraftAsync(image.Id, Settings());
        var draft = new VisionDraft(image.Id, image.Name, result.Description, result.Tags, "vision", Analysis: result.Analysis);
        await (await fixture.Scope.Vision.OpenVisionCheckpointAsync()).StageAsync(draft);
        var databasePath = Path.Combine(fixture.VaultPath, "gallery.db");

        await fixture.Scope.Vault.CloseVaultAsync();
        await SetCheckpointWriteTriggerAsync(databasePath, backend, create: true);
        await fixture.Scope.Vault.OpenExistingVaultAsync(fixture.VaultPath);
        await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);

        var checkpoint = await fixture.Scope.Vision.OpenVisionCheckpointAsync();
        var persistedDraft = Assert.Single(checkpoint.State.Drafts);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => checkpoint.ApproveDraftAsync(persistedDraft));
        Assert.Contains("checkpoint-save-injected", GetExceptionMessages(failure), StringComparison.Ordinal);

        await fixture.Scope.Vault.CloseVaultAsync();
        await SetCheckpointWriteTriggerAsync(databasePath, backend, create: false);
        await fixture.Scope.Vault.OpenExistingVaultAsync(fixture.VaultPath);
        await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var reopened = await fixture.Scope.Vision.OpenVisionCheckpointAsync();
        var stillPending = Assert.Single(reopened.State.Drafts);
        Assert.Equal(persistedDraft.Description, stillPending.Description);
        Assert.Equal(persistedDraft.Analysis!.Json, stillPending.Analysis!.Json);
        var unchanged = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Empty(unchanged.Description);
        Assert.Null(unchanged.VisionFacts);
        Assert.Empty(await fixture.Scope.Tags.LoadTagNamesAsync());
        Assert.Null(await fixture.Scope.Vision.ReadVisionAnalysisAsync(image.Id));

        var approved = await reopened.ApproveDraftAsync(stillPending);
        Assert.NotNull(approved);
        Assert.Empty(reopened.State.Drafts);
        Assert.Equal(result.Description, approved.Description);
        Assert.Equal(result.Analysis!.Json, (await fixture.Scope.Vision.ReadVisionAnalysisAsync(image.Id))!.Json);
        Assert.Null(await reopened.ApproveDraftAsync(stillPending));
    }

    private static async Task SetCheckpointWriteTriggerAsync(string databasePath, CatalogBackend backend, bool create)
    {
        byte[]? vaultKey = null;
        byte[]? catalogKey = null;
        try
        {
            System.Data.Common.DbConnection connection;
            if (backend == CatalogBackend.Turso)
            {
                vaultKey = await new VaultStore(Path.GetDirectoryName(databasePath)!).UnlockWithPassphraseAsync(TestVault.Passphrase);
                catalogKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, 32,
                    info: "DiffusionGenGallery/catalog/turso/v1"u8.ToArray());
                var builder = new System.Data.Common.DbConnectionStringBuilder
                {
                    ["Data Source"] = databasePath,
                    ["Pooling"] = false,
                    ["Encryption Cipher"] = "aes256gcm",
                    ["Encryption Key"] = Convert.ToHexString(catalogKey)
                };
                connection = new Turso.TursoConnection(builder.ConnectionString);
            }
            else connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");

            await using (connection)
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = create
                    ? """
                        CREATE TRIGGER reject_vision_checkpoint_update BEFORE UPDATE OF payload ON settings
                        WHEN OLD.id = 'vision-checkpoint'
                        BEGIN SELECT RAISE(ABORT, 'checkpoint-save-injected'); END;
                        """
                    : "DROP TRIGGER reject_vision_checkpoint_update";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (vaultKey is not null) CryptographicOperations.ZeroMemory(vaultKey);
            if (catalogKey is not null) CryptographicOperations.ZeroMemory(catalogKey);
        }
    }

    private static string GetExceptionMessages(Exception exception) =>
        string.Join(" | ", EnumerateExceptions(exception).Select(error => error.Message));

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
            yield return current;
    }

    [Fact]
    public async Task StaleDraft_IsRejectedAndTagOnlyApprovalPreservesDescriptionAndAnalysis()
    {
        using var fixture = new Fixture(CatalogBackend.Sqlite);
        await fixture.InitializeAsync();
        var image = await fixture.ImportAsync();
        var result = await fixture.Scope.Vision.AnalyzeForDraftAsync(image.Id, Settings());
        await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Description = "Edited while analyzing" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Vision.ApproveVisionAsync(image.Id, result.Description, result.Tags, "vision", result.Analysis));
        await fixture.Scope.Vision.ApproveVisionAsync(image.Id, result.Description, result.Tags, "vision", result.Analysis, tagsOnly: true);
        var stored = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Equal("Edited while analyzing", stored.Description);
        Assert.Equal("", stored.AnalysisModel);
        Assert.Null(stored.VisionFacts);
        Assert.Null(await fixture.Scope.Vision.ReadVisionAnalysisAsync(image.Id));
    }

    private static VisionSettings Settings() => new() { Model = "vision", StructuredAnalysis = true, SystemPrompt = StructuredVision.DefaultSystemPrompt, AnalysisMaxTokens = 8192 };
    private sealed class ExportOwner(ExportFile file) : IDisposable
    {
        public ExportFile File { get; } = file;
        public void Dispose() => File.ClearContent();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Request { get; private set; }
        public int RequestCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            Request = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Document } } } })) };
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath().Replace("/var/", "/private/var/"), "structured-vision-" + Guid.NewGuid().ToString("N"));
        public string VaultPath => Path.Combine(Root, "vault");
        public Handler Handler { get; } = new();
        public GalleryScope Scope { get; }
        private readonly CatalogBackend backend;
        public Fixture(CatalogBackend backend)
        {
            this.backend = backend;
            Scope = GalleryScope.Create(VaultPath, new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(Root, "state") }, new HttpClient(Handler));
        }
        public IGalleryStore Store(string root) => backend == CatalogBackend.Sqlite ? new GalleryStore(Path.Combine(root, "gallery.db")) : new Gallery.Core.Infrastructure.Persistence.Turso.TursoGalleryStore(Path.Combine(root, "gallery.db"));
        public Task<string> InitializeAsync() => Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        public Task<ImageRecord> ImportAsync() => Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "scene.png", "test");
        public void Dispose()
        {
            Scope.Dispose();
            Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            Handler.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
