using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class VideoProbeMetadataException : GalleryTechnicalException
{
    public VideoProbeMetadataException() { }
    public VideoProbeMetadataException(string message) : base(message) { }
    public VideoProbeMetadataException(string message, Exception innerException) : base(message, innerException) { }
    public VideoProbeMetadataException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    protected override string ReasonCore => "Video metadata could not be read.";
}
