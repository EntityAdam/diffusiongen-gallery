using Gallery.Core.Exceptions;

namespace Gallery.Core.Services.Processors;

internal sealed class FolderImportFailureLogEvent() : LogEventBase("FolderImport.Failed");
