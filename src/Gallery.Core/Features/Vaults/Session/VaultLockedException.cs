using System.Diagnostics.CodeAnalysis;

namespace Gallery.Core.Exceptions;

[ExcludeFromCodeCoverage]
public sealed class VaultLockedException : GalleryBusinessException
{
    public VaultLockedException() { }
    public VaultLockedException(string message) : base(message) { }
    public VaultLockedException(string message, Exception exception) : base(message, exception) { }
    public VaultLockedException(string message, LogEventBase logEvent,
        ContextualData? contextualData = null, Exception? exception = null)
        : base(message, logEvent, contextualData, exception) { }

    protected override string ReasonCore => "Unlock the gallery first.";
}
