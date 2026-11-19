using Gallery.Core.Models;

namespace Gallery.Web.Components.Features.Media;

internal sealed class ViewerReviewProgress
{
    private readonly HashSet<string> reviewed = [];
    private readonly HashSet<string> skipped = [];
    private readonly Stack<UndoEntry> undo = [];
    private bool hasSkipped;
    private bool celebrated;

    public int ReviewedCount => reviewed.Count;
    public int SkippedCount => skipped.Count;
    public int ProcessedCount => ReviewedCount + SkippedCount;
    public bool IsComplete { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public UndoEntry? PendingUndo => undo.TryPeek(out var entry) ? entry : null;

    public void RecordDecision(string id, int index, ReviewState previousState)
    {
        undo.Push(new(index, previousState, reviewed.Contains(id), skipped.Contains(id)));
        reviewed.Add(id);
        skipped.Remove(id);
    }

    public void RecordUndo(string id)
    {
        var entry = undo.Pop();
        if (entry.WasReviewed) reviewed.Add(id); else reviewed.Remove(id);
        if (entry.WasSkipped) skipped.Add(id); else skipped.Remove(id);
        Reopen();
    }

    public void RecordSkip(string id)
    {
        hasSkipped = true;
        if (!reviewed.Contains(id)) skipped.Add(id);
    }

    public int? Advance(IReadOnlyList<ImageRecord> images, int currentIndex)
    {
        if (ProcessedCount == images.Count)
        {
            IsComplete = true;
            return null;
        }

        // Browse forward until the end, then return to the first untouched item.
        if (currentIndex + 1 < images.Count) return currentIndex + 1;
        for (var index = 0; index < images.Count; index++)
        {
            var id = images[index].Id;
            if (!reviewed.Contains(id) && !skipped.Contains(id)) return index;
        }
        return null;
    }

    public void Reopen() => IsComplete = false;

    public bool TakeCelebration(int imageCount)
    {
        if (!IsComplete || hasSkipped || celebrated || imageCount == 0 || ReviewedCount != imageCount)
            return false;
        celebrated = true;
        return true;
    }

    internal sealed record UndoEntry(int Index, ReviewState State, bool WasReviewed, bool WasSkipped);
}
