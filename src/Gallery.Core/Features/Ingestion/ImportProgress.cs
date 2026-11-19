namespace Gallery.Core.Models;

public sealed record ImportProgress(int? Total, int Completed, int Imported, int Failed, string Status);
