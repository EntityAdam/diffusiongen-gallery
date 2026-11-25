using Gallery.Core.Exceptions;

namespace Gallery.Core.Features.Media.Removal.Managers.Diagnostics;

internal sealed class RemovalFailureLogEvent() : LogEventBase("GalleryRemoval.Failed");
