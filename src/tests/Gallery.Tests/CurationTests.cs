using Gallery.Core.Processors;
using System.Text;
using Gallery.Web.Exceptions;
using Gallery.Web.Models;
using Gallery.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

// Curation scenarios enter the production gallery boundary; independent persisted-state arrangements
// exercise migrated cached hashes without invoking the duplicate-rule implementation.
[Trait("Category", "acceptance")]
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
    public async Task DuplicateScan_WhenImagesAreResizedOrDifferent_ThenGroupsOnlyTheResizedCopy()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var original = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(320, 240, 0)), "original.png", "folder");
        var resized = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(160, 120, 0)), "resized.png", "folder");
        var different = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(320, 240, 1)), "different.png", "folder");
        var expectedIds = new[] { original.Id, resized.Id }.Order(StringComparer.Ordinal).ToArray();

        // Act
        var scan = await fixture.Service.ScanDuplicatesAsync();

        // Assert - the actual perceptual threshold groups the resized copy and excludes the different image
        Assert.Equal(expectedIds, Assert.Single(scan.Groups).ImageIds.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(different.Id, scan.Groups.SelectMany(group => group.ImageIds));
    }

    [Fact]
    public async Task DuplicateScan_WhenNearCopiesAreImported_GroupsKeepsAndEncryptsTheCachedResult()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var initialCache = await fixture.Service.CachedDuplicateScanAsync();
        var small = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(200, 150, 0)), "small.png", "folder");
        var large = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(400, 300, 0)), "large.png", "folder");
        var other = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(200, 150, 1)), "other.png", "folder");
        var progress = new List<(int Completed, int Total)>();

        // Act
        var scan = await fixture.Service.ScanDuplicatesAsync(new SyncProgress(progress.Add));
        var initialProgress = progress.ToArray();
        var cachedResult = await fixture.Service.CachedDuplicateScanAsync();
        var encryptedSettings = new Dictionary<string, string>();
        foreach (var id in new[] { "duplicate-scan", "perceptual-hashes" })
            encryptedSettings[id] = Encoding.UTF8.GetString((await fixture.RawStore.ReadSettingsAsync(id))!);
        await fixture.Service.UpdateAsync(small.Id, image => image with { Rating = 5 });
        progress.Clear();
        var rescan = await fixture.Service.ScanDuplicatesAsync(new SyncProgress(progress.Add));

        // Assert
        Assert.Null(initialCache);
        Assert.Equal(3, scan.ScannedCount);
        var group = Assert.Single(scan.Groups);
        Assert.Equal(large.Id, group.KeepId);
        Assert.Equal([large.Id, small.Id], group.ImageIds.ToArray());
        Assert.DoesNotContain(other.Id, group.ImageIds);
        Assert.False(group.Exact);
        Assert.Equal(new FileInfo(small.StoredPath).Length, group.ReclaimableBytes);
        Assert.Equal((3, 3), initialProgress[^1]);

        Assert.NotNull(cachedResult);
        var cached = cachedResult!;
        Assert.Equal(scan.ScannedAt, cached.ScannedAt);
        Assert.Equal(group.ImageIds.ToArray(),
            Assert.Single(cached.Groups).ImageIds.ToArray());
        foreach (var (id, setting) in encryptedSettings)
            Assert.DoesNotContain(small.Id, setting);

        // A rescan reuses cached fingerprints and reflects curation changes in the keeper.
        Assert.Equal((3, 3), progress[0]);
        Assert.Equal(small.Id, Assert.Single(rescan.Groups).KeepId);
    }

    [Fact]
    public void KeeperSelection_WhenCurationAndResolutionDiffer_PrefersTheHighestPriorityRecord()
    {
        // Arrange
        using var fixture = new TestVault();
        var now = DateTimeOffset.UtcNow;
        var bigger = new WebImageRecord { Id = "big", Width = 400, Height = 300, ImportedAt = now };
        var smaller = new WebImageRecord { Id = "small", Width = 200, Height = 150, ImportedAt = now.AddDays(-1) };
        // Act
        var resolutionKeeper = fixture.Service.PickDuplicateKeeper([smaller, bigger]).Id;
        smaller = smaller with { Favorite = true };
        var favoriteKeeper = fixture.Service.PickDuplicateKeeper([smaller, bigger]).Id;
        bigger = bigger with { Rating = 3 };
        var ratingKeeper = fixture.Service.PickDuplicateKeeper([smaller, bigger]).Id;
        bigger = bigger with { MarkedForDeletion = true };
        var unmarkedKeeper = fixture.Service.PickDuplicateKeeper([smaller, bigger]).Id;
        var twin = new WebImageRecord { Id = "twin", Width = 200, Height = 150, Favorite = true, ImportedAt = now.AddDays(-2) };
        var oldestKeeper = fixture.Service.PickDuplicateKeeper([smaller, twin]).Id;

        // Assert
        Assert.Equal("big", resolutionKeeper);
        Assert.Equal("small", favoriteKeeper);
        Assert.Equal("big", ratingKeeper);
        Assert.Equal("small", unmarkedKeeper);
        Assert.Equal("twin", oldestKeeper);
    }

    // Exact pixels override shape differences; a perceptual match alone does not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateScan_WhenShapesDiffer_ThenRequiresIdenticalPixels(bool identicalPixels)
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var wide = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(400, 100, 0)), "wide.png", "folder");
        var square = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(100, 100, 1)), "square.png", "folder");
        var arrangedSquare = identicalPixels ? square with { PngSha256 = wide.PngSha256 } : square;
        await DuplicateCacheArrangement.ArrangeAsync(fixture, [(wide, identicalPixels ? 0UL : 42UL), (arrangedSquare, identicalPixels ? ulong.MaxValue : 42UL)]);

        // Act
        var scan = await fixture.Service.ScanDuplicatesAsync();

        // Assert - migrated catalog/cached hashes still enforce the shape and exact-pixel rule
        if (identicalPixels)
        {
            var group = Assert.Single(scan.Groups);
            Assert.True(group.Exact);
        }
        else
            Assert.Empty(scan.Groups);
    }

    [Fact]
    public async Task Preferences_WhenSavedAndReloaded_RoundTripEncryptedAndNormalizeUnknownValues()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();

        // Act
        var initial = await fixture.Service.LoadPreferencesAsync();
        var defaultDensity = initial.GridDensity;
        var defaultAutoLock = initial.AutoLockMinutes;
        await fixture.Service.SavePreferencesAsync(new WebUiPreferences { GridDensity = "compact", AutoLockMinutes = 30 });
        var savedPreferences = await fixture.Service.LoadPreferencesAsync();
        var storedPreferences = await fixture.RawStore.ReadSettingsAsync("ui-preferences");
        await fixture.Service.SavePreferencesAsync(new WebUiPreferences { GridDensity = "gigantic", AutoLockMinutes = -5 });
        var invalidFallback = await fixture.Service.LoadPreferencesAsync();
        await fixture.Service.SavePreferencesAsync(new WebUiPreferences { GridDensity = "compact", AutoLockMinutes = 0 });
        var disabledAutoLock = await fixture.Service.LoadPreferencesAsync();
        await fixture.Service.SavePreferencesAsync(new WebUiPreferences { GridDensity = "large", AutoLockMinutes = 99999 });
        var clampedHigh = await fixture.Service.LoadPreferencesAsync();

        // Assert
        Assert.Equal("comfortable", defaultDensity);
        Assert.Equal(15, defaultAutoLock);
        Assert.Equal("compact", savedPreferences.GridDensity);
        Assert.Equal(30, savedPreferences.AutoLockMinutes);
        Assert.DoesNotContain("compact", Encoding.UTF8.GetString(storedPreferences!));
        Assert.Equal("comfortable", invalidFallback.GridDensity);
        Assert.Equal(15, invalidFallback.AutoLockMinutes);
        Assert.Equal(0, disabledAutoLock.AutoLockMinutes);
        Assert.Equal(1440, clampedHigh.AutoLockMinutes);
    }

    [Fact]
    public async Task BulkUpdate_WhenSomeIdsAreMissing_UpdatesExistingImagesAndReportsFailedIds()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(40, 30, 0)), "a.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(40, 30, 1)), "b.png", "folder");

        // Act
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Service.UpdateManyAsync([first.Id, second.Id, "missing", first.Id], image =>
        {
            return image with { Favorite = true, Tags = fixture.Service.MergeTags(image.Tags, "batch") };
        }));

        // Assert
        var recordFailure = Assert.Single(failure.InnerExceptions);
        Assert.Contains("missing", recordFailure.Message);
        Assert.IsType<InvalidOperationException>(recordFailure.InnerException);
        var images = await fixture.Service.ListAsync();
        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
            images.Select(image => image.Id).Order(StringComparer.Ordinal));
        Assert.All(images, image =>
        {
            Assert.True(image.Favorite);
            Assert.Equal("batch", image.Tags);
        });
    }

    [Fact]
    public void InitialKeepers_WhenSomeCopiesAreMarkedForDeletion_PreservesARecommendedKeeper()
    {
        // Arrange
        using var fixture = new TestVault();
        var big = new WebImageRecord { Id = "big", Width = 400, Height = 300 };
        var small = new WebImageRecord { Id = "small", Width = 200, Height = 150 };
        var tiny = new WebImageRecord { Id = "tiny", Width = 100, Height = 75 };
        // Act
        var initialKeepers = fixture.Service.InitialDuplicateKeepers([small, big, tiny]);
        tiny = tiny with { MarkedForDeletion = true };
        var partialDecisionKeepers = fixture.Service.InitialDuplicateKeepers([big, small, tiny]);
        big = big with { MarkedForDeletion = true };
        small = small with { MarkedForDeletion = true };
        var allMarkedKeepers = fixture.Service.InitialDuplicateKeepers([big, small, tiny]);

        // Assert
        Assert.Equal(["big"], initialKeepers);
        Assert.Equal(["big", "small"], partialDecisionKeepers.Order());
        Assert.Equal(["big"], allMarkedKeepers);
    }

    [Fact]
    public async Task DuplicateDecision_WhenKeepersAreChosen_UnmarksThemAndMarksTheRemainingCopies()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var a = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(40, 30, 0)), "a.png", "folder");
        var b = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(40, 30, 1)), "b.png", "folder");
        var c = await fixture.Service.ImportAsync(new MemoryStream(MakePattern(80, 60, 0)), "c.png", "folder");
        await fixture.Service.UpdateAsync(a.Id, image => image with { MarkedForDeletion = true });
        string[] group = [a.Id, b.Id, c.Id];

        // Act
        await fixture.Service.ApplyDuplicateDecisionAsync(group, new HashSet<string> { a.Id, c.Id });
        var marks = (await fixture.Service.ListAsync()).ToDictionary(image => image.Id, image => image.MarkedForDeletion);
        var emptyKeepersError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyDuplicateDecisionAsync(group, new HashSet<string>()));
        var outsideKeeperError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyDuplicateDecisionAsync(group, new HashSet<string> { "elsewhere" }));
        var afterRejectedDecisions = await fixture.Service.ListAsync();

        // Assert
        Assert.False(marks[a.Id]);
        Assert.True(marks[b.Id]);
        Assert.False(marks[c.Id]);

        Assert.IsType<InvalidOperationException>(emptyKeepersError);
        Assert.IsType<InvalidOperationException>(outsideKeeperError);
        Assert.Contains("elsewhere", outsideKeeperError.Message, StringComparison.Ordinal);
        Assert.All(group, id => Assert.Contains(id, outsideKeeperError.Message, StringComparison.Ordinal));
        var unchanged = Assert.Single(afterRejectedDecisions, image => image.Id == b.Id);
        Assert.True(unchanged.MarkedForDeletion);
    }

    private sealed class SyncProgress(Action<(int, int)> report) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => report(value);
    }
}
