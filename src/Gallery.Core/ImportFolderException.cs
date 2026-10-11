namespace Gallery.Core;

public sealed class ImportFolderException(string folder, AggregateException failures)
    : Exception($"Importing folder '{folder}' completed with one or more file failures.", failures)
{
    public AggregateException Failures { get; } = failures;
}
