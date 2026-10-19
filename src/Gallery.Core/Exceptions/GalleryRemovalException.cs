using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class GalleryRemovalException : GalleryTechnicalException
{
    public GalleryRemovalException() { }
    public GalleryRemovalException(string message) : base(message) { }
    public GalleryRemovalException(string message, Exception exception) : base(message, exception) { }
    public GalleryRemovalException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "An image could not be completely removed.";
}
