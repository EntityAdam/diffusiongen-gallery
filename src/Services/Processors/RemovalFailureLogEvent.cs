using Gallery.Core;

namespace Gallery.Services.Processors;

internal sealed class RemovalFailureLogEvent() : LogEventBase("GalleryRemoval.Failed");
