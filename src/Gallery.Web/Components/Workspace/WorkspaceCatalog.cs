using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

// One instance belongs to one unlocked workspace. Background publications use
// its renderer dispatcher, and generations prevent old loads surviving a reload/lock.
public sealed class WorkspaceCatalog(GalleryService gallery, ILogger logger, Func<Func<Task>, Task> dispatch) : IDisposable
{
    public IReadOnlyList<ImageRecord> Images { get; private set; } = [];
    public IReadOnlyDictionary<string, ImageRecord> ImagesById { get; private set; } = new Dictionary<string, ImageRecord>();
    public CollectionCatalog Collections { get; private set; } = CollectionCatalog.Empty;
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public IEnumerable<string> AllTags => Tags.Concat(Images.SelectMany(image => SplitTags(image.Tags)))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
    public bool IsPartial { get; private set; }
    public bool IsLoading { get; private set; }
    public string LoadError { get; private set; } = "";
    public event Func<Task>? Changed;

    private long generation;
    private CancellationTokenSource? cancellation;
    private Task load = Task.CompletedTask;
    private bool disposed;

    private static IEnumerable<string> SplitTags(string tags) =>
        tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private bool IsCurrent(long revision) => !disposed && revision == generation && gallery.IsUnlocked;
    private Task NotifyAsync() => Changed?.Invoke() ?? Task.CompletedTask;

    public async Task OpenAsync(int pageSize, bool trash)
    {
        CancelLoad();
        var revision = generation;
        var initial = await gallery.LoadInitialPageAsync(pageSize, trash);
        if (!IsCurrent(revision)) return;
        await PublishAsync(initial.Records, revision, !initial.IsComplete || !initial.IsCurrent);
        if (IsCurrent(revision) && IsPartial) StartLoad();
    }

    public async Task ReloadAsync()
    {
        CancelLoad();
        var revision = generation;
        List<ImageRecord> records;
        var partial = IsPartial;
        if (partial) records = await gallery.ReloadRecordsAsync(Images.Select(image => image.Id));
        else
        {
            var snapshot = await gallery.LoadCatalogAsync();
            if (!IsCurrent(revision)) return;
            records = snapshot.Records;
            partial = !snapshot.IsCurrent;
        }
        if (!IsCurrent(revision)) return;
        await PublishAsync(records, revision, partial);
        if (IsCurrent(revision) && IsPartial) StartLoad();
    }

    private async Task PublishAsync(IReadOnlyList<ImageRecord> records, long revision, bool partial)
    {
        var collections = await gallery.LoadCollectionCatalogAsync();
        var tags = await gallery.LoadTagNamesAsync();
        if (!IsCurrent(revision)) return;
        Images = records;
        ImagesById = records.ToDictionary(image => image.Id);
        Collections = collections;
        Tags = tags;
        IsPartial = partial;
        IsLoading = false;
        LoadError = "";
        await NotifyAsync();
    }

    public void Update(ImageRecord updated)
    {
        if (disposed || !gallery.IsUnlocked) return;
        Tags = AllTags.Concat(SplitTags(updated.Tags)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Images = Images.Select(image => image.Id == updated.Id ? updated : image).ToArray();
        var updatedIndex = ImagesById.ToDictionary(pair => pair.Key, pair => pair.Value);
        updatedIndex[updated.Id] = updated;
        ImagesById = updatedIndex;
        if (IsPartial) StartLoad();
    }

    public void RetryLoad()
    {
        if (IsPartial && !IsLoading) StartLoad();
    }

    private void CancelLoad()
    {
        generation++;
        cancellation?.Cancel();
        IsLoading = false;
    }

    private void StartLoad()
    {
        var previous = load;
        CancelLoad();
        if (disposed || !gallery.IsUnlocked) return;
        var next = new CancellationTokenSource();
        cancellation = next;
        var revision = generation;
        IsLoading = true;
        LoadError = "";
        load = FinishLoadAsync(previous, revision, next);
    }

    private async Task FinishLoadAsync(Task previous, long revision, CancellationTokenSource source)
    {
        try
        {
            await previous;
            // Prioritize the initial render and thumbnail requests.
            await Task.Delay(250, source.Token);
            var snapshot = await gallery.LoadCatalogAsync(source.Token);
            if (!IsCurrent(revision) || source.IsCancellationRequested) return;
            await dispatch(async () =>
            {
                if (!IsCurrent(revision) || source.IsCancellationRequested) return;
                if (!snapshot.IsCurrent) { StartLoad(); return; }
                await PublishAsync(snapshot.Records, revision, partial: false);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(revision)) return;
            await dispatch(async () =>
            {
                if (!IsCurrent(revision)) return;
                logger.LogError(exception, "Background catalog load failed");
                LoadError = "The rest of the library could not be loaded. The initial page is still available.";
                IsLoading = false;
                await NotifyAsync();
            });
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) { cancellation = null; load = Task.CompletedTask; }
            source.Dispose();
        }
    }

    public void Dispose()
    {
        disposed = true;
        CancelLoad();
        Changed = null;
        Images = [];
        ImagesById = new Dictionary<string, ImageRecord>();
        Collections = CollectionCatalog.Empty;
        Tags = [];
        IsPartial = false;
        LoadError = "";
    }
}
