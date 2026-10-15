namespace Gallery.Web.Models;

public sealed record WebLibraryJobStatus(bool IsRunning, string Workflow, string Message, int Suggestions, bool CanStop);
