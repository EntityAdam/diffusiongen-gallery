using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class ThumbnailNotFoundException : GalleryBusinessException
{
    public ThumbnailNotFoundException() { }
    public ThumbnailNotFoundException(string message) : base(message) { }
    public ThumbnailNotFoundException(string message, Exception exception) : base(message, exception) { }
    public ThumbnailNotFoundException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "Media thumbnail does not exist.";
    protected override System.Net.HttpStatusCode HttpStatusCodeCore => System.Net.HttpStatusCode.NotFound;
}
