using System.Diagnostics.CodeAnalysis;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class ImportFolderException : GalleryTechnicalException
{
    public ImportFolderException() { }
    public ImportFolderException(string message) : base(message) { }
    public ImportFolderException(string message, Exception innerException) : base(message, innerException) { }
    public ImportFolderException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "One or more files could not be imported.";
    public AggregateException Failures => InnerException as AggregateException
        ?? (InnerException is null ? new AggregateException() : new AggregateException(InnerException));
}
