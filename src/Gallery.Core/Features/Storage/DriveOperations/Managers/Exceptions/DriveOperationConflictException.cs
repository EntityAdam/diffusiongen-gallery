using System.Diagnostics.CodeAnalysis;
using Gallery.Core.Exceptions;

namespace Gallery.Core.Features.Storage.DriveOperations;

[ExcludeFromCodeCoverage]
public sealed class DriveOperationConflictException : GalleryTechnicalException
{
    public DriveOperationConflictException() { }
    public DriveOperationConflictException(string message) : base(message) { }
    public DriveOperationConflictException(string message, Exception innerException) : base(message, innerException) { }
    public DriveOperationConflictException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? innerException = null)
        : base(message, logEvent, contextualData, innerException) { }

    protected override string ReasonCore => "Queued gallery work conflicted with the current files.";
}
