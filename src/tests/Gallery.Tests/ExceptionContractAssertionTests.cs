using System.Net;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Diagnostics.Models;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ExceptionContractAssertionTests
{
    [Fact]
    public void Context_WhenAddingSupportedScalars_ThenPreservesValuesAndExposesOnlyTypedAdditions()
    {
        var date = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var context = new ContextualData()
            .Add("String", "value").Add("Int", 1).Add("Long", 2L)
            .Add("Float", 3.5f).Add("Double", 4.5d).Add("Decimal", 5.5m)
            .Add("Bool", true).Add("DateTime", date);

        Assert.Equal("value", context.ValuesByName["String"]);
        Assert.Equal(1, context.ValuesByName["Int"]);
        Assert.Equal(2L, context.ValuesByName["Long"]);
        Assert.Equal(3.5f, context.ValuesByName["Float"]);
        Assert.Equal(4.5d, context.ValuesByName["Double"]);
        Assert.Equal(5.5m, context.ValuesByName["Decimal"]);
        Assert.Equal(true, context.ValuesByName["Bool"]);
        Assert.Equal(date, context.ValuesByName["DateTime"]);
        var addTypes = typeof(ContextualData).GetMethods().Where(method => method.Name == "Add")
            .Select(method => method.GetParameters()[1].ParameterType).ToHashSet();
        Assert.True(addTypes.SetEquals([typeof(string), typeof(int), typeof(long), typeof(float),
            typeof(double), typeof(decimal), typeof(bool), typeof(DateTime)]));
        Assert.All(typeof(ContextualData).GetConstructors(), constructor => Assert.Empty(constructor.GetParameters()));
        Assert.Throws<ArgumentNullException>(() => context.Add("Null", (string)null!));
    }

    [Fact]
    public void Context_WhenReusedOrReplaced_ThenExceptionKeepsItsSnapshotAndDiagnosticRendering()
    {
        var context = new ContextualData().Add("Import.Detail", "original");
        var exception = CaptureThrown(new ImportFolderException("Cannot import", new LogEventGallery("Gallery.ImportFolder"), context));
        context.Add("Import.Detail", "changed");
        var returned = exception.ContextualDataByName;
        returned.Add("Import.Detail", "returned change");

        Assert.Equal("original", exception.ContextualDataByName.ValuesByName["Import.Detail"]);
        Assert.Equal("changed", context.ValuesByName["Import.Detail"]);
        Assert.Equal("returned change", returned.ValuesByName["Import.Detail"]);
        Assert.Equal(500, exception.ContextualDataByName.ValuesByName["HttpStatusCode"]);
        Assert.Contains("HttpStatusCode: InternalServerError", exception.ToDiagnosticString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(exception.ToJson());
        Assert.Equal(500, json.RootElement.GetProperty("HttpStatusCode").GetInt32());
        Assert.Equal("original", json.RootElement.GetProperty("Import.Detail").GetString());
    }

    [Fact]
    public void Contract_WhenIndependentExpectationsMatch_ThenAcceptsCauseStackAndSerializedContext()
    {
        var nested = "{\"values\":[1,2],\"label\":\"original\"}";
        var expected = ExpectedExceptionContract.Create(typeof(ImportFolderException), ["cannot", "import"],
            HttpStatusCode.InternalServerError, Severity.Error, "One or more files could not be imported.",
            "Gallery.ImportFolder", new Dictionary<string, object?> { ["Import.Detail"] = nested });
        var actual = CaptureThrown(new ImportFolderException("Cannot import this folder", new LogEventGallery("Gallery.ImportFolder"),
            new ContextualData().Add("Import.Detail", nested),
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
