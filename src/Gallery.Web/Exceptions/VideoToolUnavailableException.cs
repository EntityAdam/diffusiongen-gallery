using System.Diagnostics.CodeAnalysis;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class VideoToolUnavailableException : GalleryTechnicalException
{
    public VideoToolUnavailableException() { }
    public VideoToolUnavailableException(string message) : base(message) { }
    public VideoToolUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    public VideoToolUnavailableException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "Required video tools are unavailable.";
}
