namespace Gallery.Web.Models;

public sealed record LibraryJobStatus(bool IsRunning, string Workflow, string Message, int Suggestions, bool CanStop);
