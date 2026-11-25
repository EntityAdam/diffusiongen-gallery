using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public abstract class GalleryException : Exception
{
    private const string StackTraceKey = "StackTrace";

    protected GalleryException() { }
    protected GalleryException(string message) : base(message) { }
    protected GalleryException(string message, Exception exception) : base(message, exception) { }
    protected GalleryException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, exception)
    {
        LogEvent = logEvent;
        additionalDiagnosticValuesByName = contextualData is null ? new ContextualData() : new ContextualData(contextualData);
    }

    private readonly ContextualData additionalDiagnosticValuesByName = new();

    public string Reason => ReasonCore;
    protected abstract string ReasonCore { get; }
    public HttpStatusCode HttpStatusCode => HttpStatusCodeCore;
    protected abstract HttpStatusCode HttpStatusCodeCore { get; }
    public LogEventBase? LogEvent { get; }
    public Severity Severity => Severity.Error;
    public ContextualData ContextualDataByName => BuildDiagnosticContext();
    public string ToJson() => JsonSerializer.Serialize(ContextualDataByName.ValuesByName);
    public string ToDiagnosticString() => string.Join(Environment.NewLine,
        ContextualDataByName.ValuesByName.Where(pair => pair.Key != StackTraceKey)
            .Select(pair => $"{pair.Key}: {(pair.Key == "HttpStatusCode" ? HttpStatusCode : pair.Value)}")
            .Concat(StackTrace is null ? [] : new[] { $"StackTrace: {StackTrace}" }));

    private ContextualData BuildDiagnosticContext()
    {
        var diagnosticValuesByName = new ContextualData(additionalDiagnosticValuesByName)
            .Add("ApplicationName", "Gallery")
            .Add("ExceptionType", GetType().Name)
            .Add("Reason", Reason)
            .Add("Severity", Severity.ToString())
            .Add("HttpStatusCode", (int)HttpStatusCode)
            .Add("Message", Message);
        if (LogEvent is not null) diagnosticValuesByName = diagnosticValuesByName.Add("LogEvent", LogEvent.Value);
        if (InnerException is not null) diagnosticValuesByName = diagnosticValuesByName.Add("CausedBy", InnerException.GetType().Name);
        if (StackTrace is not null) diagnosticValuesByName = diagnosticValuesByName.Add(StackTraceKey, StackTrace);
        return diagnosticValuesByName;
    }
    public ExceptionCause? Cause => InnerException is { } exception
        ? new(exception.GetType().Name, exception.Message, exception.StackTrace) : null;
}

[ExcludeFromCodeCoverage]
public abstract class GalleryBusinessException : GalleryException
{
    protected GalleryBusinessException() { }
    protected GalleryBusinessException(string message) : base(message) { }
    protected GalleryBusinessException(string message, Exception exception) : base(message, exception) { }
    protected GalleryBusinessException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override HttpStatusCode HttpStatusCodeCore => System.Net.HttpStatusCode.BadRequest;
}

[ExcludeFromCodeCoverage]
public abstract class GalleryTechnicalException : GalleryException
{
    protected GalleryTechnicalException() { }
    protected GalleryTechnicalException(string message) : base(message) { }
    protected GalleryTechnicalException(string message, Exception exception) : base(message, exception) { }
    protected GalleryTechnicalException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override HttpStatusCode HttpStatusCodeCore => System.Net.HttpStatusCode.InternalServerError;
}
