using Gallery.Core.Exceptions;

namespace Gallery.Core.Features.Ingestion;

internal sealed class FolderImportFailureLogEvent() : LogEventBase("FolderImport.Failed");
