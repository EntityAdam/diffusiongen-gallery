using System.Diagnostics.CodeAnalysis;

namespace Gallery.Web.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class VaultLockedException : GalleryBusinessException
{
    public VaultLockedException() { }
    public VaultLockedException(string message) : base(message) { }
    public VaultLockedException(string message, Exception innerException) : base(message, innerException) { }
    public VaultLockedException(string message, LogEventBase logEvent,
        ContextualData? contextualDataByName = null, Exception? innerException = null)
        : base(message, logEvent, contextualDataByName, innerException) { }

    public override string Reason => "Unlock the gallery first.";
}
