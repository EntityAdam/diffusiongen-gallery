using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class StoredMetadataFormatException : GalleryTechnicalException
{
    public StoredMetadataFormatException() { }
    public StoredMetadataFormatException(string message) : base(message) { }
    public StoredMetadataFormatException(string message, Exception exception) : base(message, exception) { }
    public StoredMetadataFormatException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "Stored generation metadata is invalid.";
}
