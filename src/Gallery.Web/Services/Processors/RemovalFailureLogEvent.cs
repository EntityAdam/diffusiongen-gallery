using Gallery.Core;

namespace Gallery.Web.Services.Processors;

internal sealed class RemovalFailureLogEvent() : LogEventBase("GalleryRemoval.Failed");
