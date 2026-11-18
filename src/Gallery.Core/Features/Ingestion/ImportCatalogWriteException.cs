namespace Gallery.Core.Exceptions;

public sealed class ImportCatalogWriteException : GalleryTechnicalException
{
    public ImportCatalogWriteException() { }
    public ImportCatalogWriteException(string message) : base(message) { }
    public ImportCatalogWriteException(string message, Exception innerException) : base(message, innerException) { }
    public ImportCatalogWriteException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    protected override string ReasonCore => "The gallery import batch could not be committed.";
}
