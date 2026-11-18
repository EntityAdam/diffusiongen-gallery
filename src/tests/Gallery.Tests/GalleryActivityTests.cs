using Gallery.Core.Models;
using Gallery.Web.Components.Features.Ingestion;
using Gallery.Web.Components.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

public sealed class GalleryActivityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnlockDuringImport_KeepsRemainingOperationBusy(bool finishImportFirst)
    {
        var activity = new GalleryActivity();
        var finishImport = new TaskCompletionSource();
        var finishUnlock = new TaskCompletionSource();
        var import = activity.RunAsync("Importing", async () =>
        {
            activity.Ingestion.IsRunning = true;
            await finishImport.Task;
            activity.Ingestion.IsRunning = false;
        }, NullLogger.Instance);
        var unlock = activity.RunAsync("Unlocking", () => finishUnlock.Task, NullLogger.Instance, allowDuringImport: true);

        (finishImportFirst ? finishImport : finishUnlock).SetResult();
        await (finishImportFirst ? import : unlock);
        Assert.True(activity.Busy);
        Assert.Equal(finishImportFirst ? "Unlocking" : "Importing", activity.Progress);

        (finishImportFirst ? finishUnlock : finishImport).SetResult();
        await Task.WhenAll(import, unlock);
        Assert.False(activity.Busy);
        Assert.Empty(activity.Progress);
    }

    [Fact]
    public void LockDuringImport_ClearsSourceAndDeletionChoiceWhileKeepingProgress()
    {
        var progress = new ImportProgress(10, 2, 2, 0, "Ingesting");
        var session = new IngestionSession { SourcePath = "private folder", DeleteSources = true, IsRunning = true, Progress = progress };
        session.ResetOnLock();
        Assert.Empty(session.SourcePath);
        Assert.False(session.DeleteSources);
        Assert.Equal(progress, session.Progress);
        session.IsRunning = false;
        session.ResetOnLock();
        Assert.Null(session.Progress);
    }
}
