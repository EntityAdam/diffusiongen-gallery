using System.Text;
using Gallery.Core;
using Gallery.Models;
using Gallery.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

// Primitive hash/keeper tests isolate ranking and threshold boundaries that end-to-end scans cannot pin.
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
    public void DifferenceHash_WhenImagesAreResizedOrDifferent_MatchesOnlyTheResizedCopy()
    {
        // Arrange
        var original = DuplicateRules.DifferenceHash(MakePattern(320, 240, 0));
        var resized = DuplicateRules.DifferenceHash(MakePattern(160, 120, 0));
        var different = DuplicateRules.DifferenceHash(MakePattern(320, 240, 1));

        // Act
        var resizedDistance = DuplicateRules.HammingDistance(original, resized);
        var differentDistance = DuplicateRules.HammingDistance(original, different);

        // Assert
        TestAssertions.True(resizedDistance <= DuplicateRules.Threshold,
            $"the resized-copy distance ({resizedDistance}) is at most threshold {DuplicateRules.Threshold}");
        TestAssertions.True(differentDistance > DuplicateRules.Threshold,
            $"the different-image distance ({differentDistance}) is greater than threshold {DuplicateRules.Threshold}");
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
            encryptedSettings[id] = Encoding.UTF8.GetString((await fixture.Store.ReadSettingsAsync(id))!);
        await fixture.Service.UpdateAsync(small.Id, image => image.Rating = 5);
        progress.Clear();
        var rescan = await fixture.Service.ScanDuplicatesAsync(new SyncProgress(progress.Add));

        // Assert
        TestAssertions.Null(initialCache, "no scan has run yet");
        Assert.Equal(3, scan.ScannedCount);
        var group = TestAssertions.Single(scan.Groups, "the resized copies form one duplicate group");
        Assert.Equal(large.Id, group.KeepId);
        Assert.Equal([large.Id, small.Id], group.ImageIds.ToArray());
        TestAssertions.DoesNotContain(other.Id, group.ImageIds, "the visually different image is not in the duplicate group");
        TestAssertions.False(group.Exact, "the resized copies are perceptual rather than byte-identical duplicates");
        Assert.Equal(new FileInfo(small.StoredPath).Length, group.ReclaimableBytes);
        Assert.Equal((3, 3), initialProgress[^1]);

        var cached = TestAssertions.NotNull(cachedResult, "the completed scan is cached");
        Assert.Equal(scan.ScannedAt, cached.ScannedAt);
        Assert.Equal(group.ImageIds.ToArray(),
            TestAssertions.Single(cached.Groups, "cached scan retains one duplicate group").ImageIds.ToArray());
        foreach (var (id, setting) in encryptedSettings)
            TestAssertions.DoesNotContain(small.Id, setting, $"encrypted setting {id} does not expose the image id");

        // A rescan reuses cached fingerprints and reflects curation changes in the keeper.
        Assert.Equal((3, 3), progress[0]);
        Assert.Equal(small.Id, TestAssertions.Single(rescan.Groups, "rescan retains the duplicate group").KeepId);
    }

    [Fact]
    public void KeeperSelection_WhenCurationAndResolutionDiffer_PrefersTheHighestPriorityRecord()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var bigger = new ImageRecord { Id = "big", Width = 400, Height = 300, ImportedAt = now };
        var smaller = new ImageRecord { Id = "small", Width = 200, Height = 150, ImportedAt = now.AddDays(-1) };
        // Act
        var resolutionKeeper = DuplicateRules.PickKeeper([smaller, bigger]).Id;
        smaller = smaller with { Favorite = true };
        var favoriteKeeper = DuplicateRules.PickKeeper([smaller, bigger]).Id;
        bigger = bigger with { Rating = 3 };
        var ratingKeeper = DuplicateRules.PickKeeper([smaller, bigger]).Id;
        bigger = bigger with { MarkedForDeletion = true };
        var unmarkedKeeper = DuplicateRules.PickKeeper([smaller, bigger]).Id;
        var twin = new ImageRecord { Id = "twin", Width = 200, Height = 150, Favorite = true, ImportedAt = now.AddDays(-2) };
        var oldestKeeper = DuplicateRules.PickKeeper([smaller, twin]).Id;

        // Assert
        Assert.Equal("big", resolutionKeeper);
        Assert.Equal("small", favoriteKeeper);
        Assert.Equal("big", ratingKeeper);
        Assert.Equal("small", unmarkedKeeper);
        Assert.Equal("twin", oldestKeeper);
    }

    [Fact]
    public void DuplicateGrouping_WhenShapesDiffer_RequiresIdenticalPixels()
    {
        // Arrange
        var wide = new ImageRecord { Id = "wide", Width = 400, Height = 100, PngSha256 = "A" };
        var square = new ImageRecord { Id = "square", Width = 100, Height = 100, PngSha256 = "B" };
        var copy = new ImageRecord { Id = "copy", Width = 100, Height = 100, PngSha256 = "A" };

        // Act
        var unrelatedGroups = DuplicateRules.GroupDuplicates([wide, square], _ => 42UL, _ => null);
        var exactGroups = DuplicateRules.GroupDuplicates([wide, copy],
            image => image.Id == "wide" ? 0UL : ulong.MaxValue, _ => null);

        // Assert
        TestAssertions.Empty(unrelatedGroups, "different-shaped non-identical images are not grouped");
        var exact = TestAssertions.Single(exactGroups,
            "byte-identical images form one group even with different shapes");
        TestAssertions.True(exact.Exact, "identical pixels are grouped as an exact duplicate");
    }

    [Fact]
    public async Task Preferences_WhenSavedAndReloaded_RoundTripEncryptedAndNormalizeUnknownValues()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();

        // Act
        var defaultDensity = (await fixture.Service.LoadPreferencesAsync()).GridDensity;
        await fixture.Service.SavePreferencesAsync(new UiPreferences { GridDensity = "compact" });
        var savedDensity = (await fixture.Service.LoadPreferencesAsync()).GridDensity;
        var storedPreferences = await fixture.Store.ReadSettingsAsync("ui-preferences");
        await fixture.Service.SavePreferencesAsync(new UiPreferences { GridDensity = "gigantic" });
        var normalizedDensity = (await fixture.Service.LoadPreferencesAsync()).GridDensity;

        // Assert
        Assert.Equal("comfortable", defaultDensity);
        Assert.Equal("compact", savedDensity);
        TestAssertions.DoesNotContain("compact", Encoding.UTF8.GetString(storedPreferences!),
            "encrypted preference settings do not expose the grid density");
        Assert.Equal("comfortable", normalizedDensity);
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
            image.Favorite = true;
            image.Tags = LibraryRules.MergeTags(image.Tags, "batch");
        }));

        // Assert
        var recordFailure = TestAssertions.Single(failure.InnerExceptions, "only the missing record fails to update");
        TestAssertions.Contains("missing", recordFailure.Message, "the aggregate reports the failed record id");
        Assert.IsType<InvalidOperationException>(recordFailure.InnerException);
        var images = await fixture.Service.ListAsync();
        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
            images.Select(image => image.Id).Order(StringComparer.Ordinal));
        Assert.All(images, image =>
        {
            TestAssertions.True(image.Favorite, $"record {image.Id} receives the favorite update");
            Assert.Equal("batch", image.Tags);
        });
    }

    [Fact]
    public void InitialKeepers_WhenSomeCopiesAreMarkedForDeletion_PreservesARecommendedKeeper()
    {
        // Arrange
        var big = new ImageRecord { Id = "big", Width = 400, Height = 300 };
        var small = new ImageRecord { Id = "small", Width = 200, Height = 150 };
        var tiny = new ImageRecord { Id = "tiny", Width = 100, Height = 75 };
        // Act
        var initialKeepers = DuplicateRules.InitialKeepers([small, big, tiny]);
        tiny = tiny with { MarkedForDeletion = true };
        var partialDecisionKeepers = DuplicateRules.InitialKeepers([big, small, tiny]);
        big = big with { MarkedForDeletion = true };
        small = small with { MarkedForDeletion = true };
        var allMarkedKeepers = DuplicateRules.InitialKeepers([big, small, tiny]);

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
        await fixture.Service.UpdateAsync(a.Id, image => image.MarkedForDeletion = true);
        string[] group = [a.Id, b.Id, c.Id];

        // Act
        await fixture.Service.ApplyDuplicateDecisionAsync(group, new HashSet<string> { a.Id, c.Id });
        var marks = (await fixture.Service.ListAsync()).ToDictionary(image => image.Id, image => image.MarkedForDeletion);
        var emptyKeepersError = await Record.ExceptionAsync(() =>
            fixture.Service.ApplyDuplicateDecisionAsync(group, new HashSet<string>()));
        var outsideKeeperError = await Record.ExceptionAsync(() =>
            fixture.Service.ApplyDuplicateDecisionAsync(group, new HashSet<string> { "elsewhere" }));
        var afterRejectedDecisions = await fixture.Service.ListAsync();

        // Assert
        TestAssertions.False(marks[a.Id], "the chosen first copy is not marked for deletion");
        TestAssertions.True(marks[b.Id], "the unchosen copy is marked for deletion");
        TestAssertions.False(marks[c.Id], "the chosen third copy is not marked for deletion");

        Assert.IsType<InvalidOperationException>(emptyKeepersError);
        Assert.IsType<InvalidOperationException>(outsideKeeperError);
        var unchanged = TestAssertions.Single(afterRejectedDecisions.Where(image => image.Id == b.Id),
            $"the original duplicate record {b.Id} remains after rejected decisions");
        TestAssertions.True(unchanged.MarkedForDeletion,
            "the earlier valid duplicate decision remains applied");
    }

    private sealed class SyncProgress(Action<(int, int)> report) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => report(value);
    }
}
