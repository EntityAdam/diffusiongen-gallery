namespace Gallery.Core.Models;

public enum ExistingOriginalAction { Retain, Delete }
public enum MissingOriginalAction { RestorePng, Discard }

public sealed record RemovalItem(string Id, string Name, string OriginalPath, bool OriginalExists);
