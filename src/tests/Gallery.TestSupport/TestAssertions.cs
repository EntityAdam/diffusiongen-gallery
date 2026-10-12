using Xunit;
using Gallery.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gallery.Tests;

internal static class TestAssertions
{
    public static void True(bool actual, string expected) =>
        Assert.True(actual, $"Expected: {expected}; Actual: {actual}.");

    public static void False(bool actual, string expected) =>
        Assert.False(actual, $"Expected: {expected}; Actual: {actual}.");

    public static void Contains(string expected, string actual, string behavior) =>
        Assert.True(actual.Contains(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (containing \"{expected}\"); Actual: \"{actual}\".");

    public static void StartsWith(string expected, string actual, string behavior) =>
        Assert.True(actual.StartsWith(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (starting with \"{expected}\"); Actual: \"{actual}\".");

    public static void EndsWith(string expected, string actual, string behavior) =>
        Assert.True(actual.EndsWith(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (ending with \"{expected}\"); Actual: \"{actual}\".");

    public static void DoesNotContain(string expected, string actual, string behavior) =>
        Assert.False(actual.Contains(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (not containing \"{expected}\"); Actual: \"{actual}\".");

    public static void DoesNotContain<T>(T expected, IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.False(values.Contains(expected),
            $"Expected: {behavior} (excluding \"{expected}\"); Actual: [{string.Join(", ", values)}].");
    }

    public static void Contains<T>(T expected, IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Contains(expected),
            $"Expected: {behavior} (including \"{expected}\"); Actual: [{string.Join(", ", values)}].");
    }

    public static void Contains<T>(IEnumerable<T> actual, Predicate<T> predicate, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Any(value => predicate(value)),
            $"Expected: {behavior}; Actual: [{string.Join(", ", values)}].");
    }

    // Scenario actuals are fully evaluated snapshots; Count and indexing are part of this contract.
    public static T Single<T>(IReadOnlyList<T> actual, string behavior)
    {
        Assert.True(actual.Count == 1,
            $"Expected: exactly one value because {behavior}; Actual: {actual.Count} values [{string.Join(", ", actual)}].");
        return actual[0];
    }

    public static T Single<T>(IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Length == 1,
            $"Expected: exactly one value because {behavior}; Actual: {values.Length} values [{string.Join(", ", values)}].");
        return values[0];
    }

    public static void Null(object? actual, string behavior) =>
        Assert.True(actual is null, $"Expected: null because {behavior}; Actual: {actual ?? "<null>"}.");

    public static T NotNull<T>(T? actual, string behavior) where T : class
    {
        Assert.True(actual is not null, $"Expected: a non-null value because {behavior}; Actual: {actual?.ToString() ?? "<null>"}.");
        return actual!;
    }

    public static void Empty<T>(IReadOnlyCollection<T> actual, string behavior) =>
        Assert.True(actual.Count == 0,
            $"Expected: no values because {behavior}; Actual: {actual.Count} values [{string.Join(", ", actual)}].");

    public static void Empty<T>(IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Length == 0,
            $"Expected: no values because {behavior}; Actual: {values.Length} values [{string.Join(", ", values)}].");
    }

    public static T Exception<T>(ExpectedException expected, Exception? actual) where T : Exception
    {
        var exception = Assert.IsType<T>(actual);
        var discrepancies = new List<string>();
        CollectMessageDiscrepancies(expected, exception, discrepancies);
        CollectPropagationDiscrepancy(expected, exception, discrepancies);
        if (exception is GalleryException galleryException)
            CollectDomainDiscrepancies(expected, galleryException, discrepancies);
        else if (expected.HttpStatusCode is not null || expected.Severity is not null
            || expected.Reason is not null || expected.LogEventValue is not null || expected.ContextualDataByName.Count > 0)
            discrepancies.Add("The framework exception does not provide the requested domain metadata/context contract.");
        Assert.True(discrepancies.Count == 0,
            $"Expected: {expected.Behavior}; Actual: {exception.GetType().Name}: \"{exception.Message}\"; "
            + string.Join(Environment.NewLine, discrepancies));
        return exception;
    }

    private static void CollectMessageDiscrepancies(ExpectedException expected, Exception actual, List<string> discrepancies)
    {
        string[] missingParts = expected.MessageParts.Where(part =>
            !actual.Message.Contains(part, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (missingParts.Length > 0) discrepancies.Add($"Missing: [{string.Join(", ", missingParts)}].");
        string[] leakedParts = expected.ForbiddenMessageParts.Where(part =>
            actual.Message.Contains(part, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (leakedParts.Length > 0) discrepancies.Add($"Unexpected message phrases: [{string.Join(", ", leakedParts)}].");
    }

    private static void CollectPropagationDiscrepancy(ExpectedException expected, Exception actual, List<string> discrepancies)
    {
        if (expected.PropagatedInstance is not null && !ReferenceEquals(expected.PropagatedInstance, actual))
            discrepancies.Add("The arranged exception instance did not propagate unchanged.");
    }

    private static void CollectDomainDiscrepancies(ExpectedException expected, GalleryException actual, List<string> discrepancies)
    {
        CollectMetadataDiscrepancies(expected, actual, discrepancies);
        var context = actual.ContextualDataByName.ValuesByName;
        foreach (var (key, value) in expected.ContextualDataByName)
        {
            if (!context.TryGetValue(key, out var actualValue)) discrepancies.Add($"Context key '{key}' is absent.");
            else if (!Equals(value, actualValue)) discrepancies.Add($"Context '{key}' expected '{value}', actual '{actualValue}'.");
        }
        CollectRenderingDiscrepancies(actual, discrepancies);
    }

    private static void CollectMetadataDiscrepancies(ExpectedException expected, GalleryException actual, List<string> discrepancies)
    {
        int statusFamily = (int)actual.HttpStatusCode / 100;
        int expectedFamily = actual is GalleryBusinessException ? 4 : 5;
        if (statusFamily != expectedFamily) discrepancies.Add($"Status family expected {expectedFamily}xx, actual {actual.HttpStatusCode}.");
        if (expected.HttpStatusCode is { } expectedStatus && actual.HttpStatusCode != expectedStatus)
            discrepancies.Add($"HttpStatusCode expected {expectedStatus}, actual {actual.HttpStatusCode}.");
        if (expected.Severity is { } expectedSeverity && actual.Severity != expectedSeverity)
            discrepancies.Add($"Severity expected {expectedSeverity}, actual {actual.Severity}.");
        if (!Enum.IsDefined(actual.HttpStatusCode)) discrepancies.Add("HttpStatusCode is undefined.");
        if (!Enum.IsDefined(actual.Severity)) discrepancies.Add("Severity is undefined.");
        if (string.IsNullOrWhiteSpace(actual.Reason)) discrepancies.Add("Reason is empty.");
        if (expected.Reason is not null && actual.Reason != expected.Reason)
            discrepancies.Add($"Reason expected '{expected.Reason}', actual '{actual.Reason}'.");
        if (actual.LogEvent is null) discrepancies.Add("The authored exception has no throw-step log event.");
        if (expected.LogEventValue is not null && actual.LogEvent?.Value != expected.LogEventValue)
            discrepancies.Add($"LogEvent expected '{expected.LogEventValue}', actual '{actual.LogEvent?.Value}'.");
    }

    private static void CollectRenderingDiscrepancies(GalleryException actual, List<string> discrepancies)
    {
        var context = actual.ContextualDataByName.ValuesByName;
        string diagnosticText = actual.ToDiagnosticString();
        foreach (var (key, value) in context)
            if (!diagnosticText.Contains($"{key}: {value}", StringComparison.Ordinal))
                discrepancies.Add($"Diagnostic text omits context '{key}: {value}'.");
        JsonNode? renderedJson = JsonNode.Parse(actual.ToJson());
        JsonNode? expectedJson = JsonSerializer.SerializeToNode(context);
        if (!JsonNode.DeepEquals(expectedJson, renderedJson)) discrepancies.Add("Diagnostic JSON differs from the complete context.");
        if (actual.StackTrace is { } stackTrace && !diagnosticText.EndsWith(stackTrace, StringComparison.Ordinal))
            discrepancies.Add("The exception stack trace is not rendered last.");
        if (actual.InnerException is { } cause)
        {
            if (!context.TryGetValue("CausedBy", out var causedBy) || !Equals(cause.GetType().Name, causedBy))
                discrepancies.Add("CausedBy does not preserve the inner exception type.");
            if (actual.Cause?.ExceptionType != cause.GetType().Name || actual.Cause.Message != cause.Message
                || actual.Cause.StackTrace != cause.StackTrace)
                discrepancies.Add("Cause does not preserve the inner exception diagnostic data.");
        }
    }
}
