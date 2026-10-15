using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public abstract class GalleryException : Exception
{
    protected GalleryException() { }
    protected GalleryException(string message) : base(message) { }
    protected GalleryException(string message, Exception innerException) : base(message, innerException) { }
    protected GalleryException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, innerException)
    {
        LogEvent = logEvent;
        additionalContext = contextualDataByName?.ValuesByName ?? ImmutableDictionary<string, object>.Empty;
    }

    private readonly ImmutableDictionary<string, object> additionalContext = ImmutableDictionary<string, object>.Empty;

    public abstract string Reason { get; }
    public abstract HttpStatusCode HttpStatusCode { get; }
    public LogEventBase? LogEvent { get; }
    public Severity Severity => Severity.Error;
    public ContextualData ContextualDataByName => BuildDiagnosticContext();
    public string ToJson() => JsonSerializer.Serialize(ContextualDataByName.ValuesByName);
    public string ToDiagnosticString() => string.Join(Environment.NewLine,
        ContextualDataByName.ValuesByName.Where(pair => pair.Key != "StackTrace")
            .Select(pair => $"{pair.Key}: {pair.Value}")
            .Concat(StackTrace is null ? [] : new[] { $"StackTrace: {StackTrace}" }));

    private ContextualData BuildDiagnosticContext()
    {
        var entries = additionalContext.ToBuilder();
        entries["ApplicationName"] = "Gallery";
        entries["ExceptionType"] = GetType().Name;
        entries["Reason"] = Reason;
        entries["Severity"] = Severity.ToString();
        entries["HttpStatusCode"] = HttpStatusCode;
        entries["Message"] = Message;
        if (LogEvent is not null) entries["LogEvent"] = LogEvent.Value;
        if (InnerException is not null) entries["CausedBy"] = InnerException.GetType().Name;
        if (StackTrace is not null) entries["StackTrace"] = StackTrace;
        return new(entries.ToImmutable());
    }
    public ExceptionCause? Cause => InnerException is { } cause
        ? new(cause.GetType().Name, cause.Message, cause.StackTrace) : null;
}

[ExcludeFromCodeCoverage]
public abstract class GalleryBusinessException : GalleryException
{
    protected GalleryBusinessException() { }
    protected GalleryBusinessException(string message) : base(message) { }
    protected GalleryBusinessException(string message, Exception innerException) : base(message, innerException) { }
    protected GalleryBusinessException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override HttpStatusCode HttpStatusCode => System.Net.HttpStatusCode.BadRequest;
}

[ExcludeFromCodeCoverage]
public abstract class GalleryTechnicalException : GalleryException
{
    protected GalleryTechnicalException() { }
    protected GalleryTechnicalException(string message) : base(message) { }
    protected GalleryTechnicalException(string message, Exception innerException) : base(message, innerException) { }
    protected GalleryTechnicalException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override HttpStatusCode HttpStatusCode => System.Net.HttpStatusCode.InternalServerError;
}
