using System.Collections.Immutable;
using System.Text.Json;
using Gallery.Core.Exceptions;
using Xunit;

namespace Gallery.Tests;

internal static class AsserterExceptionContract
{
    public static void AssertMatches(ExpectedExceptionContract expected, GalleryException actual)
    {
        var discrepancies = new List<string>();
        Compare("Exception type", expected.ExceptionType.FullName, actual.GetType().FullName, discrepancies);
        discrepancies.AddRange(FindMissingMessagePhrases(expected.MessagePhrases, actual.Message));
        Compare("HttpStatusCode", expected.HttpStatusCode, actual.HttpStatusCode, discrepancies);
        Compare("Severity", expected.Severity, actual.Severity, discrepancies);
        Compare("Reason", expected.Reason, actual.Reason, discrepancies);
        Compare("LogEvent", expected.LogEvent, actual.LogEvent?.Value, discrepancies);

        var context = actual.ContextualDataByName.ValuesByName;
        foreach (var (key, expectedValue) in expected.Context)
        {
            if (!context.TryGetValue(key, out var value))
                discrepancies.Add($"Context '{key}': Expected: {expectedValue}; Actual: <missing>.");
            else if (!JsonElement.DeepEquals(expectedValue, JsonSerializer.SerializeToElement(value)))
                discrepancies.Add($"Context '{key}': Expected: {expectedValue}; Actual: {JsonSerializer.Serialize(value)}.");
        }

        var runtimeKeys = new[] { "ApplicationName", "ExceptionType", "Reason", "Severity", "HttpStatusCode", "Message" }
            .ToHashSet(StringComparer.Ordinal);
        if (actual.LogEvent is not null) runtimeKeys.Add("LogEvent");
        if (actual.StackTrace is not null) runtimeKeys.Add("StackTrace");
        if (actual.InnerException is not null) runtimeKeys.Add("CausedBy");
        foreach (var key in context.Keys.Except(expected.Context.Keys).Except(runtimeKeys))
            discrepancies.Add($"Context key: Expected: an independently arranged expectation for '{key}'; Actual: unasserted contextual entry.");
        Compare("Context Message", actual.Message, context.GetValueOrDefault("Message"), discrepancies);

        var expectedDiagnostics = ExpectedExceptionDiagnostics.Create(context, actual.StackTrace, actual.InnerException?.GetType().Name);
        discrepancies.AddRange(FindDiagnosticDiscrepancies(expectedDiagnostics,
            new(actual.ToDiagnosticString(), actual.ToJson())));
        Assert.True(discrepancies.Count == 0,
            "EXCEPTION CONTRACT ASSERTION FAILED\n" + string.Join("\n", discrepancies));
    }

    public static void AssertMessageContains(IEnumerable<string> expectedPhrases, string actualMessage)
    {
        var discrepancies = FindMissingMessagePhrases(expectedPhrases, actualMessage);
        Assert.True(discrepancies.Count == 0,
            "EXCEPTION MESSAGE ASSERTION FAILED\n" + string.Join("\n", discrepancies));
    }

    public static void AssertDiagnosticsMatch(ExpectedExceptionDiagnostics expected, ActualExceptionDiagnostics actual)
    {
        var discrepancies = FindDiagnosticDiscrepancies(expected, actual);
        Assert.True(discrepancies.Count == 0,
            "EXCEPTION DIAGNOSTICS ASSERTION FAILED\n" + string.Join("\n", discrepancies));
    }

    private static List<string> FindMissingMessagePhrases(IEnumerable<string> expectedPhrases, string actualMessage) =>
        expectedPhrases.Where(phrase => !actualMessage.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .Select(phrase => $"Message: Expected phrase: '{phrase}'; Actual: '{actualMessage}'.").ToList();

    private static List<string> FindDiagnosticDiscrepancies(ExpectedExceptionDiagnostics expected, ActualExceptionDiagnostics actual)
    {
        var discrepancies = new List<string>();
        foreach (var (key, renderedValue) in expected.RenderedContext)
        {
            var expectedLine = $"{key}: {renderedValue}";
            if (!actual.Text.Contains(expectedLine, StringComparison.Ordinal))
                discrepancies.Add($"Diagnostic text: Expected: '{expectedLine}'; Actual: '{actual.Text}'.");
        }
        if (expected.StackTrace is not null && !actual.Text.Contains(expected.StackTrace, StringComparison.Ordinal))
            discrepancies.Add($"Diagnostic stack trace: Expected: '{expected.StackTrace}'; Actual: '{actual.Text}'.");
        if (expected.CauseType is not null
            && (!expected.Context.TryGetValue("CausedBy", out var cause)
                || cause.ValueKind != JsonValueKind.String || cause.GetString() != expected.CauseType))
            discrepancies.Add($"CausedBy: Expected: '{expected.CauseType}'; Actual: '{expected.Context.GetValueOrDefault("CausedBy")}'.");
        try
        {
            using var document = JsonDocument.Parse(actual.Json);
            var expectedJson = JsonSerializer.SerializeToElement(expected.Context);
            if (!JsonElement.DeepEquals(expectedJson, document.RootElement))
                discrepancies.Add($"Diagnostic JSON context: Expected: {expectedJson}; Actual: {document.RootElement}.");
        }
        catch (JsonException exception)
        {
            discrepancies.Add($"Diagnostic JSON: Expected: parseable context object; Actual: '{actual.Json}' ({exception.Message}).");
        }
        return discrepancies;
    }

    private static void Compare(string name, object? expected, object? actual, List<string> discrepancies)
    {
        if (!Equals(expected, actual))
            discrepancies.Add($"{name}: Expected: {expected ?? "<null>"}; Actual: {actual ?? "<null>"}.");
    }
}
