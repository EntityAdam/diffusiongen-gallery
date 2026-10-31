using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Stress;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

namespace Gallery.Tests;

public sealed class StressBackendTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("simulate-catalog")]
    [InlineData("measure-open")]
    [InlineData("open-once")]
    [InlineData("large-images")]
    [InlineData("large-ingest-once")]
    [InlineData("viewer-once")]
    public void Backend_option_accepts_named_backends_and_rejects_typos(string command)
    {
        Assert.Null(new StressOptions([command]).RequestedCatalogBackend);
        Assert.Equal(CatalogBackend.Turso, new StressOptions([command, "--catalog-backend", "Turso"]).RequestedCatalogBackend);
        Assert.Equal(CatalogBackend.Sqlite, new StressOptions([command, "--catalog-backend", "sqlite"]).RequestedCatalogBackend);
        Assert.Throws<ArgumentException>(() => new StressOptions([command, "--catalog-backend", "tursso"]));
        Assert.Throws<ArgumentException>(() => new StressOptions([command, "--catalog-backend", "1"]));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Resume_detects_the_header_and_rejects_a_backend_change_before_writing_credentials(CatalogBackend backend)
    {
        var root = Safety.CanonicalPath(Path.Combine(Path.GetTempPath(), "gallery-stress-backend-" + Guid.NewGuid().ToString("N")));
        var vault = Path.Combine(root, "vault");
        var workspace = Path.Combine(root, "workspace");
        try
        {
            var created = await new VaultStore(vault).CreateAsync(TestVault.Passphrase, backend: backend);
            CryptographicOperations.ZeroMemory(created.Key);
            var automatic = new StressOptions(["build", "--workspace", workspace]);
            Assert.Equal(backend, StressVault.ResolveBackend(automatic, vault));
            var other = backend == CatalogBackend.Turso ? "sqlite" : "turso";
            var mismatched = new StressOptions(["build", "--workspace", workspace, "--catalog-backend", other]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => StressVault.OpenOrCreateAsync(mismatched, vault, default));
            Assert.False(Directory.Exists(workspace));
            Assert.False(File.Exists(Path.Combine(vault, "gallery.db")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Legacy_checkpoint_without_backend_deserializes_as_SQLite()
    {
        var json = JsonSerializer.Serialize(new { Vault = "vault", Staging = "stage", Media = "vault", Seed = 42, Sizes = "fixed:1MB", Metadata = "fixed:8KB" });
        Assert.Equal(CatalogBackend.Sqlite, JsonSerializer.Deserialize<BuildState>(json)!.CatalogBackend);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite, false)]
    [InlineData(CatalogBackend.Turso, false)]
    [InlineData(CatalogBackend.Turso, true)]
    public async Task Interrupted_build_reuses_verified_sources_without_recreating_deleted_committed_sources(CatalogBackend backend, bool changedCommittedSource)
    {
        var root = Safety.CanonicalPath(Path.Combine(Path.GetTempPath(), "gallery-stress-resume-" + Guid.NewGuid().ToString("N")));
        var vault = Path.Combine(root, "vault");
        var workspace = Path.Combine(root, "workspace");
        var staging = Path.Combine(root, "staging");
        var batch = Path.Combine(staging, "batch-000000");
        var options = new StressOptions(["build", "--workspace", workspace, "--vault", vault, "--staging", staging,
            "--targets", "3", "--batch-files", "1", "--size-dist", "fixed:128KB", "--metadata-dist", "fixed:8KB",
            "--workers", "1", "--catalog-batch", "1", "--gen-threads", "1", "--catalog-backend", backend.ToString()]);
        try
        {
            var sources = Enumerable.Range(0, 3).Select(i => Path.Combine(batch, "000000", $"{i:D12}.png")).ToArray();
            var generated = new List<GeneratedPng>();
            foreach (var (path, index) in sources.Select((path, index) => (path, index)))
                generated.Add(await SyntheticPngWriter.WriteAsync(path, 42, index, SizeDistribution.Parse("fixed:128KB"), SizeDistribution.Parse("fixed:8KB")));
            await File.WriteAllTextAsync(Path.Combine(staging, "stress-staging.marker"), "Gallery.Stress");
            using (var service = await StressVault.OpenOrCreateAsync(options, vault, default))
            {
                await service.PauseDriveOperationsAsync();
                await using (var input = File.OpenRead(sources[0]))
                {
                    var imported = await service.ImportAsync(input, "000000000000.png", batch, sources[0]);
                    await service.VerifyImportedAsync(imported.Id);
                }
                await service.CloseVaultAsync();
            }
            if (changedCommittedSource) await File.WriteAllTextAsync(sources[0], "changed; retain this source");
            else File.Delete(sources[0]);
            await using (var receipts = await GenerationReceipts.OpenAsync(Path.Combine(workspace, "generation-000000.jsonl"),
                42, 0, 3, "fixed:128KB", "fixed:8KB", default))
                await receipts.RecordAsync(1, generated[1], default);
            var statePath = Path.Combine(workspace, "state.json");
            await StressVault.SaveAtomicAsync(statePath, new BuildState(vault, staging, vault, 42, "fixed:128KB", "fixed:8KB", Pending: 3, CatalogBackend: backend), default);
            if (changedCommittedSource)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => BuildCommand.RunAsync(options, default));
                Assert.Equal("changed; retain this source", await File.ReadAllTextAsync(sources[0]));
                Assert.Equal(3, JsonSerializer.Deserialize<BuildState>(await File.ReadAllTextAsync(statePath))!.Pending);
            }
            else
            {
                await BuildCommand.RunAsync(options, default);
                var state = JsonSerializer.Deserialize<BuildState>(await File.ReadAllTextAsync(statePath))!;
                Assert.Equal(3, state.Files);
                Assert.Equal(0, state.Pending);
                Assert.Equal(generated.Sum(item => item.Bytes), state.MediaBytes);
                Assert.False(Directory.Exists(batch));
                using var row = JsonDocument.Parse((await File.ReadAllLinesAsync(Path.Combine(workspace, "results", "ingestion.jsonl"))).Single());
                Assert.Equal(1, row.RootElement.GetProperty("committedFiles").GetInt64());
                Assert.Equal(1, row.RootElement.GetProperty("reusedFiles").GetInt64());
                Assert.Equal(1, row.RootElement.GetProperty("regeneratedFiles").GetInt64());
                await BuildCommand.RunAsync(options, default); // Completed checkpoint is idempotent.
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
