using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Gallery.Core.Exceptions;

namespace Gallery.Tests;

internal sealed record ExpectedExceptionContract(
    Type ExceptionType,
    ImmutableArray<string> MessagePhrases,
    HttpStatusCode HttpStatusCode,
    Severity Severity,
    string Reason,
    string? LogEvent,
    ImmutableDictionary<string, JsonElement> Context)
{
    public static ExpectedExceptionContract Create(
        Type exceptionType, IEnumerable<string> messagePhrases, HttpStatusCode httpStatusCode,
        Severity severity, string reason, string? logEvent = null,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        var expectedContext = context?.ToImmutableDictionary(pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal)
            ?? ImmutableDictionary<string, JsonElement>.Empty;
        expectedContext = expectedContext
            .SetItem("ApplicationName", JsonSerializer.SerializeToElement("Gallery"))
            .SetItem("ExceptionType", JsonSerializer.SerializeToElement(exceptionType.Name))
            .SetItem("Reason", JsonSerializer.SerializeToElement(reason))
            .SetItem("Severity", JsonSerializer.SerializeToElement(severity.ToString()))
            .SetItem("HttpStatusCode", JsonSerializer.SerializeToElement(httpStatusCode));
        if (logEvent is not null)
            expectedContext = expectedContext.SetItem("LogEvent", JsonSerializer.SerializeToElement(logEvent));
        return new(exceptionType, [.. messagePhrases], httpStatusCode, severity, reason, logEvent, expectedContext);
    }
}

internal sealed record ExpectedExceptionDiagnostics(
    ImmutableDictionary<string, JsonElement> Context,
    ImmutableDictionary<string, string> RenderedContext,
    string? StackTrace, string? CauseType)
{
    public static ExpectedExceptionDiagnostics Create(
        IReadOnlyDictionary<string, object> context, string? stackTrace, string? causeType)
    {
        var entries = context.ToImmutableDictionary(StringComparer.Ordinal);
        if (stackTrace is not null) entries = entries.SetItem("StackTrace", stackTrace);
        if (causeType is not null) entries = entries.SetItem("CausedBy", causeType);
        return new(entries.ToImmutableDictionary(pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal),
            entries.ToImmutableDictionary(pair => pair.Key, pair => $"{pair.Value}", StringComparer.Ordinal),
            stackTrace, causeType);
    }
}

internal sealed record ActualExceptionDiagnostics(string Text, string Json);
