using System.Diagnostics.CodeAnalysis;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class ThumbnailNotFoundException : GalleryBusinessException
{
    public ThumbnailNotFoundException() { }
    public ThumbnailNotFoundException(string message) : base(message) { }
    public ThumbnailNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    public ThumbnailNotFoundException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "Image thumbnail does not exist.";
    public override System.Net.HttpStatusCode HttpStatusCode => System.Net.HttpStatusCode.NotFound;
}
