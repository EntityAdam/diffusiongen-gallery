using Gallery.Web.Exceptions;

namespace Gallery.Web.Services.Processors;

internal sealed class RemovalFailureLogEvent() : LogEventBase("GalleryRemoval.Failed");
