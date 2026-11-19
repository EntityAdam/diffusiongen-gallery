using Gallery.Core.Exceptions;
using Gallery.Web.Components.Features.Ingestion;

namespace Gallery.Web.Components.Workspace;

// The circuit's import and operation status survive locking and reopening the workspace.
public sealed class GalleryActivity
{
    public IngestionSession Ingestion { get; } = new();
    public bool Busy { get; internal set; }
    public string Progress { get; internal set; } = "";
    public string Message { get; internal set; } = "";
    public string Error { get; internal set; } = "";

    internal event Action? Completed;
    private readonly LinkedList<string> operations = [];

    internal async Task RunAsync(string status, Func<Task> action, ILogger logger, bool allowDuringImport = false)
    {
        var nested = allowDuringImport && Busy && Ingestion.IsRunning;
        if (Busy && !nested) return;
        var operation = operations.AddLast(status);
        Busy = true;
        Progress = status;
        Error = Message = "";
        try { await action(); }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            logger.LogWarning(exception, "Gallery authentication or integrity check failed");
            Error = "Incorrect credentials, or encrypted data failed its integrity check. The operation was stopped.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Gallery operation failed");
            Error = exception is IOException or InvalidOperationException or System.Text.Json.JsonException
                or ImportFolderException or HttpRequestException or SixLabors.ImageSharp.UnknownImageFormatException
                or SixLabors.ImageSharp.InvalidImageContentException ? exception.Message
                : "The operation failed. Check the application console for details.";
        }
        finally
        {
            operations.Remove(operation);
            Busy = operations.Count > 0;
            Progress = operations.Last?.Value ?? "";
            Completed?.Invoke();
        }
    }
}
