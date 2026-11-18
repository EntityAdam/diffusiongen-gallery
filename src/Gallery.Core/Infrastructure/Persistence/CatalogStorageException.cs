namespace Gallery.Core.Exceptions;

/// <summary>Provider-neutral failure crossing the experimental catalog adapter boundary.</summary>
public sealed class CatalogStorageException(string message, Exception innerException) : GalleryTechnicalException(message, innerException)
{
    protected override string ReasonCore => "The gallery catalog operation failed.";
}
