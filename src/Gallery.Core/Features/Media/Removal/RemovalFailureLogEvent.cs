using Gallery.Core.Exceptions;

namespace Gallery.Core.Features.Media.Removal;

internal sealed class RemovalFailureLogEvent() : LogEventBase("GalleryRemoval.Failed");
