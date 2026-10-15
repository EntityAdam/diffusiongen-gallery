using System.Diagnostics.CodeAnalysis;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class StoredMetadataFormatException : GalleryTechnicalException
{
    public StoredMetadataFormatException() { }
    public StoredMetadataFormatException(string message) : base(message) { }
    public StoredMetadataFormatException(string message, Exception innerException) : base(message, innerException) { }
    public StoredMetadataFormatException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "Stored generation metadata is invalid.";
}
