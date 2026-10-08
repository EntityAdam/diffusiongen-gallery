namespace Gallery.Models;

public sealed record LibraryJobStatus(bool Running, string Workflow, string Message, int Suggestions, bool CanStop);
