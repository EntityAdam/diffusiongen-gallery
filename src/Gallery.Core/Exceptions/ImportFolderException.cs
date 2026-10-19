using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class ImportFolderException : GalleryTechnicalException
{
    public ImportFolderException() { }
    public ImportFolderException(string message) : base(message) { }
    public ImportFolderException(string message, Exception exception) : base(message, exception) { }
    public ImportFolderException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "One or more files could not be imported.";
    public AggregateException Failures => InnerException as AggregateException
        ?? (InnerException is null ? new AggregateException() : new AggregateException(InnerException));
}
