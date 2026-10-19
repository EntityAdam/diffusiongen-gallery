using Gallery.Core.Exceptions;

namespace Gallery.Core.Services.Processors;

internal sealed class RemovalFailureLogEvent() : LogEventBase("GalleryRemoval.Failed");
