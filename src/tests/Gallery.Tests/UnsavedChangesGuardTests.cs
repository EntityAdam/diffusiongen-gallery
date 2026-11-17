using Gallery.Web.Components;
using Gallery.Web.Components.Workspace;

namespace Gallery.Tests;

public sealed class UnsavedChangesGuardTests
{
    [Fact]
    public async Task ResolvesEachDirtyFeatureBeforeContinuingOnce()
    {
        var notes = new Draft("notes");
        var settings = new Draft("settings");
        var continuations = 0;
        using var guard = new UnsavedChangesGuardState(new(), () => false);
        await guard.RequestAsync([notes.Source, settings.Source], () => { continuations++; return Task.CompletedTask; });
        Assert.Equal("notes", guard.Subject);

        await guard.SaveAsync();
        Assert.Equal(1, notes.Saves);
        Assert.Equal("settings", guard.Subject);
        Assert.Equal(0, continuations);
        await guard.DiscardAsync();
        await guard.SaveAsync();
        Assert.False(guard.IsPending);
        Assert.Equal(1, settings.Discards);
        Assert.Equal(1, continuations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrStillDirtySaveRetainsDraftAndContinuation(bool saveReturnsSuccess)
    {
        var dirty = true;
        var calls = 0;
        using var guard = new UnsavedChangesGuardState(new(), () => false);
        var source = new UnsavedChangesSource("settings", () => dirty, () => false,
            () => Task.FromResult(saveReturnsSuccess), () => { dirty = false; return Task.CompletedTask; });
        await guard.RequestAsync([source], () => { calls++; return Task.CompletedTask; });
        await guard.SaveAsync();
        Assert.True(dirty);
        Assert.True(guard.IsPending);
        Assert.Equal(0, calls);
        await guard.DiscardAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BusyFeatureBlocksRequestAndKeepEditingCancelsTheContinuation()
    {
        var draft = new Draft("notes") { Busy = true };
        var continued = false;
        using var guard = new UnsavedChangesGuardState(new(), () => false);
        Task Continue() { continued = true; return Task.CompletedTask; }
        await guard.RequestAsync([draft.Source], Continue);
        Assert.False(guard.IsPending);
        Assert.False(continued);
        draft.Busy = false;
        await guard.RequestAsync([draft.Source], Continue);
        guard.KeepEditing();
        await guard.DiscardAsync();
        Assert.True(draft.Dirty);
        Assert.False(continued);
    }

    [Fact]
    public async Task DisposingDuringSavePreventsThePendingContinuation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dirty = true;
        var continued = false;
        using var guard = new UnsavedChangesGuardState(new(), () => false);
        var source = new UnsavedChangesSource("notes", () => dirty, () => false, async () =>
        {
            started.SetResult();
            var result = await finish.Task;
            dirty = false;
            return result;
        }, () => Task.CompletedTask);
        await guard.RequestAsync([source], () => { continued = true; return Task.CompletedTask; });
        var saving = guard.SaveAsync();
        await started.Task;
        guard.Dispose();
        finish.SetResult(true);
        await saving;
        Assert.False(guard.IsPending);
        Assert.False(continued);
    }

    [Fact]
    public async Task SavedContinuationCanRequestAnotherGuardedAction()
    {
        var draft = new Draft("notes");
        var continued = false;
        using var guard = new UnsavedChangesGuardState(new(), () => false);
        await guard.RequestAsync([draft.Source], () => guard.RequestAsync([draft.Source], () =>
        {
            continued = true;
            return Task.CompletedTask;
        }));
        await guard.SaveAsync();
        Assert.True(continued);
    }

    private sealed class Draft(string subject)
    {
        public bool Dirty = true;
        public bool Busy;
        public int Saves, Discards;
        public UnsavedChangesSource Source => new(subject, () => Dirty, () => Busy,
            () => { Saves++; Dirty = false; return Task.FromResult(true); },
            () => { Discards++; Dirty = false; return Task.CompletedTask; });
    }
}
