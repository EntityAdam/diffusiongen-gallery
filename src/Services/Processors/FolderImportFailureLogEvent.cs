using Gallery.Core;

namespace Gallery.Services.Processors;

internal sealed class FolderImportFailureLogEvent() : LogEventBase("FolderImport.Failed");
