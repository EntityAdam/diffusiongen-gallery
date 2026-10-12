using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core;

[ExcludeFromCodeCoverage]
public sealed class GalleryRemovalException : GalleryTechnicalException
{
    public GalleryRemovalException() { }
    public GalleryRemovalException(string message) : base(message) { }
    public GalleryRemovalException(string message, Exception innerException) : base(message, innerException) { }
    public GalleryRemovalException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "An image could not be completely removed.";
}
