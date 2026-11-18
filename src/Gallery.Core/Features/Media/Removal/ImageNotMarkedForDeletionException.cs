using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class ImageNotMarkedForDeletionException : GalleryBusinessException
{
    public ImageNotMarkedForDeletionException() { }
    public ImageNotMarkedForDeletionException(string message) : base(message) { }
    public ImageNotMarkedForDeletionException(string message, Exception exception) : base(message, exception) { }
    public ImageNotMarkedForDeletionException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "Media item must be marked for deletion.";
}
