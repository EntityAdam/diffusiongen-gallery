using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

/// <summary>Provider-neutral failure crossing the experimental catalog adapter boundary.</summary>
[ExcludeFromCodeCoverage]
public sealed class CatalogStorageException : GalleryTechnicalException
{
    public CatalogStorageException() { }
    public CatalogStorageException(string message) : base(message) { }
    public CatalogStorageException(string message, Exception innerException) : base(message, innerException) { }
    public CatalogStorageException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    protected override string ReasonCore => "The gallery catalog operation failed.";
}
