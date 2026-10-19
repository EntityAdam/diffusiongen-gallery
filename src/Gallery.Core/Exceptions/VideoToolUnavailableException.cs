using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class VideoToolUnavailableException : GalleryTechnicalException
{
    public VideoToolUnavailableException() { }
    public VideoToolUnavailableException(string message) : base(message) { }
    public VideoToolUnavailableException(string message, Exception exception) : base(message, exception) { }
    public VideoToolUnavailableException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "Required video tools are unavailable.";
}
