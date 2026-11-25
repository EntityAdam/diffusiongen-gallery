using Gallery.Core.Exceptions;

namespace Gallery.Core.Features.Ingestion.Managers.Models;

internal sealed class LogEventFolderImportFailure() : LogEventBase("FolderImport.Failed");
