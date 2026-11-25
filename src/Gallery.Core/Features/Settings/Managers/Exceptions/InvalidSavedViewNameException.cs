using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class InvalidSavedViewNameException : GalleryBusinessException
{
    public InvalidSavedViewNameException() { }
    public InvalidSavedViewNameException(string message) : base(message) { }
    public InvalidSavedViewNameException(string message, Exception exception) : base(message, exception) { }
    public InvalidSavedViewNameException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "Saved view name is invalid.";
}
