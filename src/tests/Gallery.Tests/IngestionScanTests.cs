using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Processors.Codecs;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class IngestionScanTests
{
    [Fact]
    public async Task Scan_CollectsSupportedMediaCountsAndSizes_WithRequestedRecursion()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "sources");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "image.PNG"), GalleryTests.MakePng());
        await File.WriteAllBytesAsync(Path.Combine(folder, "clip.MP4"), new byte[123]);
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored.txt"), "not media");
        await File.WriteAllBytesAsync(Path.Combine(folder, "nested", "image.webp"), new byte[42]);

        var flat = await fixture.Service.ScanFolderAsync(folder, FolderSearchScope.SelectedFolder);
        var recursive = await fixture.Service.ScanFolderAsync(folder, FolderSearchScope.Descendants);

        Assert.Equal(1, flat.ImageCount);
        Assert.Equal(1, flat.VideoCount);
        Assert.Equal(GalleryTests.MakePng().LongLength + 123, flat.SourceBytes);
        Assert.Equal(2, recursive.ImageCount);
        Assert.Equal(flat.SourceBytes + 42, recursive.SourceBytes);

        var images = await fixture.Service.EstimateIngestionSpaceAsync(recursive, true, false);
        var videos = await fixture.Service.EstimateIngestionSpaceAsync(recursive, false, true);
        Assert.Equal(2, images.FileCount);
        Assert.Equal(recursive.SourceBytes - 123, images.SelectedSourceBytes);
        Assert.Equal(1, videos.FileCount);
        Assert.Equal(123, videos.SelectedSourceBytes);
        Assert.True(images.MediaAvailableBytes > 0);
        Assert.True(images.CatalogAvailableBytes > 0);
        Assert.True(images.RequiredBytes > images.SelectedSourceBytes);
    }

    [Fact]
    public async Task Ingest_UsesOnlyCachedEnumeration_AndRespectsMediaSelection()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "sources");
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "scanned.png");
        var unselectedVideo = Path.Combine(folder, "not-selected.mp4");
        await File.WriteAllBytesAsync(original, GalleryTests.MakePng());
        await File.WriteAllBytesAsync(unselectedVideo, new byte[123]);
        var scan = await fixture.Service.ScanFolderAsync(folder, FolderSearchScope.Descendants);
        // A fresh enumeration would discover this image (and fail on the new video).
        await File.WriteAllBytesAsync(Path.Combine(folder, "later.png"), GalleryTests.MakePng(30, 20));
        await File.WriteAllBytesAsync(Path.Combine(folder, "later.mp4"), new byte[99]);
        File.Delete(unselectedVideo);
        ImportProgress? final = null;

        await fixture.Service.ImportFolderAsync(folder, scan.SearchScope, scan: scan, includeVideos: false,
            sourceRetention: SourceRetention.DeleteAfterVerification,
            reportProgress: update => { final = update; return Task.CompletedTask; });

        Assert.Equal("scanned", Assert.Single(await fixture.Service.ListAsync()).Name);
        Assert.Equal(1, final!.Total);
        Assert.Equal(1, final.Imported);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(Path.Combine(folder, "later.png")));
        Assert.True(File.Exists(Path.Combine(folder, "later.mp4")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_WhenScannedSourceChangesOrDisappears_RequiresRescan(bool remove)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "sources");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.png");
        await File.WriteAllBytesAsync(source, GalleryTests.MakePng());
        var scan = await fixture.Service.ScanFolderAsync(folder, FolderSearchScope.SelectedFolder);
        if (remove) File.Delete(source);
        else await File.WriteAllBytesAsync(source, new byte[7]);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.ImportFolderAsync(folder, scan.SearchScope, scan: scan));

        Assert.Contains("Scan the folder again", error.Message);
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [InstalledVideoToolsFact]
    public async Task Ingest_VideoOnlySelection_LeavesImagesUntouched()
    {
        using var arrangement = await RealVideoArrangement.CreateSourceAsync();
        var fixture = arrangement.TestVault;
        var folder = Path.Combine(fixture.Root, "sources");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "image.png"), GalleryTests.MakePng());
        await File.WriteAllBytesAsync(Path.Combine(folder, "clip.mp4"), arrangement.Bytes);
        var scan = await fixture.Service.ScanFolderAsync(folder, FolderSearchScope.SelectedFolder);

        await fixture.Service.ImportFolderAsync(folder, scan.SearchScope, scan: scan,
            includeImages: false, includeVideos: true, sourceRetention: SourceRetention.DeleteAfterVerification);

        Assert.True(Assert.Single(await fixture.Service.ListAsync()).IsVideo);
        Assert.True(File.Exists(Path.Combine(folder, "image.png")));
        Assert.False(File.Exists(Path.Combine(folder, "clip.mp4")));
    }

    [Fact]
    public async Task Ingest_WhenFolderOrRecursionChanges_RejectsCachedScan()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var scan = await fixture.Service.ScanFolderAsync(fixture.Root, FolderSearchScope.SelectedFolder);
        var other = Path.Combine(fixture.Root, "other");
        Directory.CreateDirectory(other);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportFolderAsync(other, scan.SearchScope, scan: scan));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportFolderAsync(scan.Folder, FolderSearchScope.Descendants, scan: scan));
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task Estimate_WhenDestinationChanges_RequiresRescan()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var scan = await fixture.Service.ScanFolderAsync(fixture.Root, FolderSearchScope.SelectedFolder);
        var destination = Path.Combine(fixture.Root, "other-media");
        Directory.CreateDirectory(destination);
        await fixture.Service.SaveMediaStorageOptionsAsync(new() { MediaDirectory = destination });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EstimateIngestionSpaceAsync(scan, true, true));
    }

    [Fact]
    public async Task EmptySelection_CannotIngest()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var scan = await fixture.Service.ScanFolderAsync(fixture.Root, FolderSearchScope.SelectedFolder);
        var space = await fixture.Service.EstimateIngestionSpaceAsync(scan, false, false);

        Assert.Equal(0, space.FileCount);
        Assert.Equal(0, space.RequiredBytes);
        Assert.Equal(0, space.EstimatedBytes);
        Assert.False(space.HasEnoughSpace);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportFolderAsync(scan.Folder, scan.SearchScope,
                scan: scan, includeImages: false, includeVideos: false));
    }

    [Fact]
    public async Task VideoSelection_WithoutTools_FailsExplicitlyBeforeImport()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "video.mp4"), new byte[123]);
        var scan = await fixture.Service.ScanFolderAsync(fixture.Root, FolderSearchScope.SelectedFolder);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ImportFolderAsync(scan.Folder, scan.SearchScope, scan: scan, includeImages: false));

        Assert.Contains("ffmpeg", error.Message);
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SpaceEstimate_RequiresEnoughSpaceAtExactBoundary(bool sameDrive)
    {
        var estimate = GalleryService.CalculateIngestionSpace(2, 10_000, long.MaxValue, long.MaxValue, sameDrive);
        var enough = GalleryService.CalculateIngestionSpace(2, 10_000,
            estimate.MediaRequiredBytes, estimate.CatalogRequiredBytes, sameDrive);
        var mediaShort = GalleryService.CalculateIngestionSpace(2, 10_000,
            estimate.MediaRequiredBytes - 1, estimate.CatalogRequiredBytes, sameDrive);
        var catalogShort = GalleryService.CalculateIngestionSpace(2, 10_000,
            estimate.MediaRequiredBytes, estimate.CatalogRequiredBytes - 1, sameDrive);

        Assert.True(enough.HasEnoughSpace);
        Assert.False(mediaShort.HasEnoughSpace);
        Assert.False(catalogShort.HasEnoughSpace);
        Assert.Equal(10_000 + 128 + 24L * 1024 * 1024, estimate.RequiredBytes);
    }

    [Fact]
    public void SpaceEstimate_DisplayUsesTypicalGrowth_NotTheSafetyReserve()
    {
        const long sourceBytes = 3_460_301;
        const int files = 4;
        var estimate = GalleryService.CalculateIngestionSpace(files, sourceBytes,
            long.MaxValue, long.MaxValue, true);
        const long expectedOverhead = files * (132L * 1024 + 72);

        Assert.Equal(sourceBytes + expectedOverhead, estimate.EstimatedBytes);
        Assert.Equal(expectedOverhead, estimate.EstimatedBytes - estimate.SelectedSourceBytes);
        Assert.InRange(estimate.EstimatedBytes / (1024d * 1024), 3.81, 3.82);
        Assert.InRange(expectedOverhead / (1024d * 1024), 0.51, 0.52);
        Assert.True(estimate.RequiredBytes > estimate.EstimatedBytes);

        var onlyEstimateFits = GalleryService.CalculateIngestionSpace(files, sourceBytes,
            estimate.EstimatedBytes, estimate.EstimatedBytes, true);
        Assert.False(onlyEstimateFits.HasEnoughSpace);
        Assert.Equal(estimate.EstimatedBytes, onlyEstimateFits.EstimatedBytes);
    }

    [Fact]
    public void SpaceEstimate_EncryptionOverheadMatchesStoredEnvelope()
    {
        var content = new byte[123];
        var envelope = VaultCrypto.Encrypt(content, new byte[32], "estimate-test");
        Assert.Equal(36, envelope.Length - content.Length);
        Assert.Equal(VaultCrypto.EnvelopeHeaderSizeBytes, envelope.Length - content.Length);
    }
}
