using System.Security.Cryptography;
using Gallery.Core.Services;
using Gallery.Core.Services.Processors.Codecs;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class IsolatedResourceCompatibilityTests
{
    [Fact]
    public void ContentFingerprint_WhenUsingTheEstablishedV1Vector_ThenPreservesStoredCatalogIdentity()
    {
        // Arrange
        var keyBytes = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var sourceHashBytes = SHA256.HashData("source image"u8);
        const string expectedFingerprint = "A565B962F1FE109C0DCE162DF7A19D0BD35FF557A72318AB8744F444A80C733B";

        // Act
        var actualFingerprint = Convert.ToHexString(VaultCrypto.ContentFingerprint(keyBytes, sourceHashBytes));

        // Assert
        Assert.Equal(expectedFingerprint, actualFingerprint);
    }

    [Theory]
    [InlineData(0d, "Wall time: 0.00 s | 0.00 files/s | 0.00 MiB/s")]
    [InlineData(2000d, "Wall time: 2.00 s | 2.00 files/s | 1.00 MiB/s")]
    public void TimingReport_WhenWallTimeIsZeroOrPositive_ThenPreservesTheExactMultiStageText(
        double wallMilliseconds, string expectedWallTimeLine)
    {
        // Arrange
        var ingestionTimingReport = new IngestionTimingReport("SSD", "HDD", 2, 8, 4, 3, 1, 2097152,
            wallMilliseconds,
            [new(IngestionStage.Decode, 2, 40, 20, 10, 30, 30),
                new(IngestionStage.PersistWait, 1, 5, 5, 5, 5, 5)]) { CatalogDrive = "RAM" };
        var expectedText = string.Join(Environment.NewLine,
            "Ingestion timings (no file or folder names recorded)",
            "Source drive: SSD | Gallery drive: HDD | Workers: 2 | CPUs: 8",
            "Catalog drive: RAM",
            "Files: 4 found, 3 imported, 1 reported issues | Source data read: 2.0 MiB",
            expectedWallTimeLine,
            "Per-file stages run on parallel workers, so stage totals can exceed wall time.",
            "Stage             Count   Total s   Mean ms    p50 ms    p95 ms    Max ms",
            "Decode                2      0.04      20.0      10.0      30.0      30.0",
            "PersistWait           1      0.01       5.0       5.0       5.0       5.0",
            "");

        // Act
        var actualText = ingestionTimingReport.ToString();

        // Assert
        Assert.Equal(expectedText, actualText);
    }
}
