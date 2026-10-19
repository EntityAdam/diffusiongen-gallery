using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class VisionResponseFormatException : GalleryTechnicalException
{
    public VisionResponseFormatException() { }
    public VisionResponseFormatException(string message) : base(message) { }
    public VisionResponseFormatException(string message, Exception innerException) : base(message, innerException) { }
    public VisionResponseFormatException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    protected override string ReasonCore => "The model returned an invalid response.";
}
