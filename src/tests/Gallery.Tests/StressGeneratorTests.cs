using System.Security.Cryptography;
using Gallery.Stress.Core;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;

namespace Gallery.Tests;

public sealed class StressGeneratorTests
{
    [Fact]
    public async Task Finder_metadata_does_not_block_staging_initialization_or_batch_completion()
    {
        var staging = Path.Combine(Path.GetTempPath(), "gallery-stress-finder-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, ".DS_Store"), "Finder metadata");
            Assert.True(StagingFiles.CanInitialize(staging));
            await File.WriteAllTextAsync(Path.Combine(staging, "stress-staging.marker"), "Gallery.Stress");
            Assert.True(StagingFiles.CanInitialize(staging));

            var batch = Path.Combine(staging, "batch-000000");
            var nested = Path.Combine(batch, "000000");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(batch, ".DS_Store"), "Finder metadata");
            await File.WriteAllTextAsync(Path.Combine(nested, ".DS_Store"), "Finder metadata");
            Assert.False(StagingFiles.HasRemainingSources(batch));
            Assert.False(StagingFiles.CanInitialize(staging));

            foreach (var name in new[] { "image.png", "image.png.gallery-delete-claim", ".unexpected", ".DS_Store.png" })
            {
                var file = Path.Combine(nested, name);
                await File.WriteAllTextAsync(file, "must be retained");
                Assert.True(StagingFiles.HasRemainingSources(batch));
                File.Delete(file);
            }
            Assert.False(StagingFiles.HasRemainingSources(batch));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    [Fact]
    public async Task Results_after_an_interrupted_append_start_on_a_new_line()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "gallery-stress-result-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new ResultWriter(workspace);
            var path = Path.Combine(workspace, "results", "interrupted.jsonl");
            await File.WriteAllTextAsync(path, "{\"unfinished\":");
            await writer.AppendAsync("interrupted", new { records = 3 });
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            using var document = System.Text.Json.JsonDocument.Parse(lines[1]);
            Assert.Equal(3, document.RootElement.GetProperty("records").GetInt32());
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task Fifty_files_are_distinct_deterministic_sized_and_importable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gallery-stress-generator-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hashes = new HashSet<string>();
            for (var index = 0; index < 50; index++)
            {
                var path = Path.Combine(directory, "sample.png");
                var generated = await SyntheticPngWriter.WriteAsync(path, 1, index, SizeDistribution.Parse("fixed:1MB"));
                var first = await File.ReadAllBytesAsync(path);
                Assert.InRange(generated.Bytes, (long)(1024 * 1024 * 0.85), (long)(1024 * 1024 * 1.15));
                Assert.True(hashes.Add(Convert.ToHexString(SHA256.HashData(first))));
                await SyntheticPngWriter.WriteAsync(path, 1, index, SizeDistribution.Parse("fixed:1MB"));
                Assert.Equal(first, await File.ReadAllBytesAsync(path));
                if (index == 0)
                {
                    using var vault = new TestVault();
                    await vault.InitializeAsync();
                    var record = await vault.Service.ImportAsync(new MemoryStream(first), "synthetic.png", "generated");
                    Assert.False(string.IsNullOrWhiteSpace(record.Generation?.Prompt));
                    Assert.False(string.IsNullOrWhiteSpace(record.Generation?.Model));
                    Assert.True(record.Generation?.HasWorkflow);
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("tier:4MB@2048x2048", 2048, 4)]
    [InlineData("tier:8MB@2944x2944", 2944, 8)]
    [InlineData("tier:16MB@4096x4096", 4096, 16)]
    [InlineData("tier:32MB@5120x5120", 5120, 32)]
    [InlineData("tier:48MB@6272x6272", 6272, 48)]
    [InlineData("tier:8MB@4096x4096", 4096, 8)]
    [InlineData("tier:32MB@4096x4096", 4096, 32)]
    public async Task Tier_preserves_dimensions_and_size(string distribution, int dimension, int megabytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "gallery-stress-tier-" + Guid.NewGuid().ToString("N"), "tier.png");
        try
        {
            var generated = await SyntheticPngWriter.WriteAsync(path, 8, 1, SizeDistribution.Parse(distribution));
            Assert.InRange(generated.Bytes, (long)(megabytes * 1024 * 1024 * 0.90), (long)(megabytes * 1024 * 1024 * 1.10));
            using var image = await Image.LoadAsync(path);
            Assert.Equal(dimension, image.Width);
            Assert.Equal(dimension, image.Height);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task Profile_is_read_only_and_reports_correct_counts()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 30)), "one.png", "");
        var db = Path.Combine(vault.Root, "gallery.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync();
        }
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(db));
        var original = File.GetAttributes(db);
        File.SetAttributes(db, original | FileAttributes.ReadOnly);
        try
        {
            var profile = await CatalogProfiler.ProfileAsync(db);
            Assert.Equal(1, profile.Rows);
            Assert.True(profile.Record.Sum > 0);
            Assert.True(profile.Thumbnail.Sum > 0);
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(db)));
        }
        finally { File.SetAttributes(db, original); }
    }

    [Fact]
    public void Safety_refuses_repository_and_unmarked_vaults()
    {
        Assert.Throws<InvalidOperationException>(() => Safety.ValidateOutputPath(Environment.CurrentDirectory));
        var repository = new DirectoryInfo(Environment.CurrentDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, ".git")) && !File.Exists(Path.Combine(repository.FullName, ".git"))) repository = repository.Parent;
        Assert.NotNull(repository);
        Assert.Throws<InvalidOperationException>(() => Safety.ValidateOutputPath(repository.Parent!.FullName));
        var applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");
        Assert.Throws<InvalidOperationException>(() => Safety.ValidateOutputPath(Path.GetDirectoryName(applicationData)!));
        Assert.Throws<InvalidOperationException>(() => Safety.RequireStressVault(Path.Combine(Path.GetTempPath(), "unmarked-stress-vault")));
    }

    [Fact]
    public void Safety_resolves_links_and_checks_path_boundaries()
    {
        var outside = Path.Combine(Path.GetTempPath(), "gallery-stress-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            // Creating symbolic links on Windows requires an elevated token or Developer Mode.
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(outside, "repo-link"), Environment.CurrentDirectory);
                Assert.Throws<InvalidOperationException>(() => Safety.ValidateOutputPath(Path.Combine(outside, "repo-link", "new-vault")));
            }
            Assert.True(Safety.Contains(outside, Path.Combine(outside, "child")));
            Assert.False(Safety.Contains(outside, outside + "-sibling"));
            Assert.True(Safety.Contains(Path.GetPathRoot(outside)!, outside));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Theory]
    [InlineData("100k", 100000)]
    [InlineData("2.1M", 2100000)]
    public void Counts_use_decimal_suffixes(string value, long expected) => Assert.Equal(expected, SizeDistribution.ParseCount(value));
}
