using System.Collections.Immutable;
using System.Net;
using Gallery.Core;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public static class ExceptionAssertionContractTests
{
    private sealed class TestLogEvent(string value) : LogEventBase(value);

    private static ImportFolderException CreateThrownFailure()
    {
        try
        {
            throw new ImportFolderException("Folder 'incoming' contains one unreadable file.",
                new TestLogEvent("Import.ReadFiles"),
                new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Folder.Path", "incoming")),
                new IOException("Image data is truncated."));
        }
        catch (ImportFolderException exception) { return exception; }
    }

    [Fact]
    public static void ExceptionOracle_WhenEveryDomainLayerMatches_ThenAcceptsTheContract()
    {
        // Arrange
        var actual = CreateThrownFailure();
        var expected = new ExpectedException("the imported-file refusal preserves its complete diagnostic contract", "incoming", "unreadable")
        {
            HttpStatusCode = HttpStatusCode.InternalServerError,
            Severity = Severity.Error,
            Reason = "One or more files could not be imported.",
            LogEventValue = "Import.ReadFiles",
            ContextualDataByName = ImmutableDictionary<string, object>.Empty.Add("Folder.Path", "incoming")
        };

        // Act
        var matched = TestAssertions.Exception<ImportFolderException>(expected, actual);

        // Assert
        Assert.Same(actual, matched);
    }

    [Fact]
    public static void ExceptionOracle_WhenEveryExpectedLayerDiffers_ThenReportsEveryDiscrepancyTogether()
    {
        // Arrange
        var actual = CreateThrownFailure();
        var expected = new ExpectedException("the diagnostic oracle identifies every independent mismatch", "missing-phrase")
        {
            HttpStatusCode = HttpStatusCode.BadRequest,
            Severity = Severity.Warning,
            Reason = "Different reason.",
            LogEventValue = "Different.Event",
            ForbiddenMessageParts = ["unreadable"],
            ContextualDataByName = ImmutableDictionary<string, object>.Empty
                .Add("Folder.Path", "other").Add("File.Name", "lost.png")
        };

        // Act
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            TestAssertions.Exception<ImportFolderException>(expected, actual));

        // Assert
        Assert.Contains("missing-phrase", failure.Message);
        Assert.Contains("Unexpected message phrases", failure.Message);
        Assert.Contains("HttpStatusCode", failure.Message);
        Assert.Contains("Severity", failure.Message);
        Assert.Contains("Reason", failure.Message);
        Assert.Contains("LogEvent", failure.Message);
        Assert.Contains("Folder.Path", failure.Message);
        Assert.Contains("File.Name", failure.Message);
    }

    [Fact]
    public static void ExceptionOracle_WhenFrameworkFailurePropagates_ThenRequiresTheArrangedInstance()
    {
        // Arrange
        var original = new IOException("Provider stream failed.");
        var replacement = new IOException("Provider stream failed.");
        var expected = new ExpectedException("the exact provider failure propagates", "stream", "failed")
        {
            PropagatedInstance = original
        };

        // Act
        var matched = TestAssertions.Exception<IOException>(expected, original);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => TestAssertions.Exception<IOException>(expected, replacement));

        // Assert
        Assert.Same(original, matched);
        Assert.Contains("instance did not propagate unchanged", failure.Message);
    }

    [Fact]
    public static void ExceptionOracle_WhenDomainMetadataIsRequestedForFrameworkFailure_ThenDoesNotInventMetadata()
    {
        // Arrange
        var original = new IOException("Provider stream failed.");
        var expected = new ExpectedException("a provider exception cannot supply an authored domain contract", "failed")
        {
            HttpStatusCode = HttpStatusCode.InternalServerError
        };

        // Act
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => TestAssertions.Exception<IOException>(expected, original));

        // Assert
        Assert.Contains("does not provide the requested domain metadata/context", failure.Message);
    }
}
