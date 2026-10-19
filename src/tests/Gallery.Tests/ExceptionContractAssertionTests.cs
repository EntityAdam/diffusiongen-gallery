using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Gallery.Core.Exceptions;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ExceptionContractAssertionTests
{
    [Fact]
    public void Contract_WhenIndependentExpectationsMatch_ThenAcceptsCauseStackAndNestedContext()
    {
        var nested = new Dictionary<string, object> { ["values"] = new[] { 1, 2 }, ["label"] = "original" };
        var expected = ExpectedExceptionContract.Create(typeof(ImportFolderException), ["cannot", "import"],
            HttpStatusCode.InternalServerError, Severity.Error, "One or more files could not be imported.",
            "Gallery.ImportFolder", new Dictionary<string, object?> { ["Import.Detail"] = nested });
        var actual = CaptureThrown(new ImportFolderException("Cannot import this folder", new GalleryLogEvent("Gallery.ImportFolder"),
            new ContextualData(ImmutableDictionary<string, object>.Empty.Add("Import.Detail", nested)),
            new AggregateException(new InvalidOperationException("provider failure"))));

        AsserterExceptionContract.AssertMatches(expected, actual);
    }

    [Fact]
    public void Contract_WhenIndependentFieldsDiffer_ThenOneFailureIncludesEveryDiscrepancy()
    {
        var expected = ExpectedExceptionContract.Create(typeof(ImportFolderException), ["first missing", "second missing"],
            HttpStatusCode.InternalServerError, Severity.Warning, "expected reason", "expected event",
            new Dictionary<string, object?> { ["Input"] = "expected input" });
        var actual = new VaultLockedException("Unlock the gallery first.");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AsserterExceptionContract.AssertMatches(expected, actual));

        Assert.Contains("Exception type: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("first missing", failure.Message, StringComparison.Ordinal);
        Assert.Contains("second missing", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Severity: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Reason: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("LogEvent: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Context 'Input': Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_WhenExpectationSourceChanges_ThenKeepsIndependentContextSnapshot()
    {
        var values = new[] { "original" };
        var expected = ExpectedExceptionContract.Create(typeof(VaultLockedException), ["unlock"],
            HttpStatusCode.BadRequest, Severity.Error, "Unlock the gallery first.",
            context: new Dictionary<string, object?> { ["Values"] = values });
        values[0] = "changed";

        Assert.Equal("original", expected.Context["Values"][0].GetString());
    }

    [Fact]
    public void Diagnostics_WhenJsonIsMalformedAndTextIsIncomplete_ThenReportsEveryIndependentFailure()
    {
        var expected = ExpectedExceptionDiagnostics.Create(new Dictionary<string, object>
        {
            ["First"] = "one", ["Second"] = "two"
        }, "at expected stack", "InvalidOperationException");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AsserterExceptionContract.AssertDiagnosticsMatch(
            expected, new("unrelated text", "{not JSON")));

        Assert.Contains("First: one", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Second: two", failure.Message, StringComparison.Ordinal);
        Assert.Contains("StackTrace: at expected stack", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CausedBy: InvalidOperationException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Expected: parseable context object", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_WhenNestedValueChanges_ThenRejectsParseableButIncorrectJson()
    {
        var nested = new Dictionary<string, object> { ["items"] = new[] { 1, 2 } };
        var expected = ExpectedExceptionDiagnostics.Create(new Dictionary<string, object> { ["Nested"] = nested }, null, null);
        var actual = new ActualExceptionDiagnostics($"Nested: {nested}", "{\"Nested\":{\"items\":[1,3]}}");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AsserterExceptionContract.AssertDiagnosticsMatch(expected, actual));

        Assert.Contains("Diagnostic JSON context: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[1,3]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_WhenCauseOrStackIsMissing_ThenRejectsEvenOtherwiseMatchingContext()
    {
        var expected = ExpectedExceptionDiagnostics.Create(new Dictionary<string, object> { ["Message"] = "failure" },
            "expected stack", "AggregateException");

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AsserterExceptionContract.AssertDiagnosticsMatch(
            expected, new("Message: failure", "{\"Message\":\"failure\"}")));

        Assert.Contains("CausedBy: AggregateException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("expected stack", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Diagnostic JSON context", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_WhenSeveralPhrasesAreAbsent_ThenReportsThemTogetherWithActualMessage()
    {
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            AsserterExceptionContract.AssertMessageContains(["offending value", "valid options"], "unhelpful error"));

        Assert.Contains("offending value", failure.Message, StringComparison.Ordinal);
        Assert.Contains("valid options", failure.Message, StringComparison.Ordinal);
        Assert.Contains("unhelpful error", failure.Message, StringComparison.Ordinal);
    }

    private static T CaptureThrown<T>(T exception) where T : Exception
    {
        try { throw exception; }
        catch (T thrown) { return thrown; }
    }
}
