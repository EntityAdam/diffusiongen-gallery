using System.Diagnostics.CodeAnalysis;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class ImageNotMarkedForDeletionException : GalleryBusinessException
{
    public ImageNotMarkedForDeletionException() { }
    public ImageNotMarkedForDeletionException(string message) : base(message) { }
    public ImageNotMarkedForDeletionException(string message, Exception innerException) : base(message, innerException) { }
    public ImageNotMarkedForDeletionException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "Image must be marked for deletion.";
}
