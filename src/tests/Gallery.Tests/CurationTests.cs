using System.Text;
using Gallery.Models;
using Gallery.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

public sealed class CurationTests
{
    private static byte[] MakePattern(int width, int height, int variant)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = variant == 0
                    ? (byte)(127 + 120 * Math.Sin(x * 6.0 / width * Math.PI) * Math.Cos(y * 3.0 / height * Math.PI))
                    : (byte)((x * 8 / width + y * 8 / height) % 2 == 0 ? 30 : 220);
                image[x, y] = new Rgba32(value, (byte)(255 - value), value, 255);
            }
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    [Fact]
    public void DifferenceHashMatchesResizedCopiesAndSeparatesDifferentImages()
    {
        var original = GalleryService.DifferenceHash(MakePattern(320, 240, 0));
        var resized = GalleryService.DifferenceHash(MakePattern(160, 120, 0));
        var different = GalleryService.DifferenceHash(MakePattern(320, 240, 1));
        Assert.True(GalleryService.HammingDistance(original, resized) <= GalleryService.DuplicateThreshold);
        Assert.True(GalleryService.HammingDistance(original, different) > GalleryService.DuplicateThreshold);
    }

    [Fact]
    public async Task DuplicateScanGroupsNearCopiesKeepsBestAndCachesEncrypted()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        Assert.Null(await fixture.Service.CachedDuplicateScanAsync());
        var small = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(200, 150, 0)), "small.png", "folder");
        var large = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(400, 300, 0)), "large.png", "folder");
        var other = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(200, 150, 1)), "other.png", "folder");
        var progress = new List<(int Completed, int Total)>();

        var scan = await fixture.Service.ScanDuplicatesAsync(new SyncProgress(progress.Add));

        Assert.Equal(3, scan.ScannedCount);
        var group = Assert.Single(scan.Groups);
        Assert.Equal(large.Id, group.KeepId);
        Assert.Equal([large.Id, small.Id], group.Ids);
        Assert.DoesNotContain(other.Id, group.Ids);
        Assert.False(group.Exact);
        Assert.Equal(new FileInfo(small.StoredPath).Length, group.ReclaimableBytes);
        Assert.Equal((3, 3), progress[^1]);

        var cached = await fixture.Service.CachedDuplicateScanAsync();
        Assert.NotNull(cached);
        Assert.Equal(scan.ScannedAt, cached.ScannedAt);
        Assert.Equal(group.Ids, Assert.Single(cached.Groups).Ids);
        foreach (var id in new[] { "duplicate-scan", "perceptual-hashes" })
            Assert.DoesNotContain(small.Id, Encoding.UTF8.GetString((await fixture.Store.ReadSettingsAsync(id))!));

        // A rescan reuses cached fingerprints and reflects curation changes in the keeper.
        await fixture.Service.UpdateAsync(small.Id, image => image.Rating = 5);
        progress.Clear();
        var rescan = await fixture.Service.ScanDuplicatesAsync(new SyncProgress(progress.Add));
        Assert.Equal((3, 3), progress[0]);
        Assert.Equal(small.Id, Assert.Single(rescan.Groups).KeepId);
    }

    [Fact]
    public void PickKeeperPrefersCurationThenResolutionThenOldest()
    {
        var now = DateTimeOffset.UtcNow;
        var bigger = new ImageRecord { Id = "big", Width = 400, Height = 300, ImportedAt = now };
        var smaller = new ImageRecord { Id = "small", Width = 200, Height = 150, ImportedAt = now.AddDays(-1) };
        Assert.Equal("big", GalleryService.PickKeeper([smaller, bigger]).Id);
        smaller.Favorite = true;
        Assert.Equal("small", GalleryService.PickKeeper([smaller, bigger]).Id);
        bigger.Rating = 3;
        Assert.Equal("big", GalleryService.PickKeeper([smaller, bigger]).Id);
        bigger.MarkedForDeletion = true;
        Assert.Equal("small", GalleryService.PickKeeper([smaller, bigger]).Id);
        var twin = new ImageRecord { Id = "twin", Width = 200, Height = 150, Favorite = true, ImportedAt = now.AddDays(-2) };
        Assert.Equal("twin", GalleryService.PickKeeper([smaller, twin]).Id);
    }

    [Fact]
    public void GroupingRequiresSimilarShapeUnlessPixelsAreIdentical()
    {
        var wide = new ImageRecord { Id = "wide", Width = 400, Height = 100, PngSha256 = "A" };
        var square = new ImageRecord { Id = "square", Width = 100, Height = 100, PngSha256 = "B" };
        Assert.Empty(GalleryService.GroupDuplicates([wide, square], _ => 42UL, GalleryService.DuplicateThreshold));
        var copy = new ImageRecord { Id = "copy", Width = 100, Height = 100, PngSha256 = "A" };
        var exact = Assert.Single(GalleryService.GroupDuplicates([wide, copy], image => image.Id == "wide" ? 0UL : ulong.MaxValue, 0));
        Assert.True(exact.Exact);
    }

    [Fact]
    public async Task PreferencesRoundTripEncryptedAndNormalizeUnknownValues()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        Assert.Equal("comfortable", (await fixture.Service.LoadPreferencesAsync()).GridDensity);
        await fixture.Service.SavePreferencesAsync(new UiPreferences { GridDensity = "compact" });
        Assert.Equal("compact", (await fixture.Service.LoadPreferencesAsync()).GridDensity);
        Assert.DoesNotContain("compact", Encoding.UTF8.GetString((await fixture.Store.ReadSettingsAsync("ui-preferences"))!));
        await fixture.Service.SavePreferencesAsync(new UiPreferences { GridDensity = "gigantic" });
        Assert.Equal("comfortable", (await fixture.Service.LoadPreferencesAsync()).GridDensity);
    }

    [Fact]
    public async Task UpdateManyAppliesToEveryImageAndCountsFailures()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(40, 30, 0)), "a.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(40, 30, 1)), "b.png", "folder");

        var failed = await fixture.Service.UpdateManyAsync([first.Id, second.Id, "missing", first.Id], image =>
        {
            image.Favorite = true;
            image.Tags = LibraryService.MergeTags(image.Tags, "batch");
        });

        Assert.Equal(1, failed);
        var images = await fixture.Service.ListAsync();
        Assert.All(images, image => { Assert.True(image.Favorite); Assert.Equal("batch", image.Tags); });
    }

    private sealed class SyncProgress(Action<(int, int)> report) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => report(value);
    }
}
