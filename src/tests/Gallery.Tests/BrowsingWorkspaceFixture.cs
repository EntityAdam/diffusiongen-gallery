using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Web.Components.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Gallery.Tests;

internal static class BrowsingWorkspaceFixture
{
    internal static List<ImageRecord> CreateImages(int count) =>
        Enumerable.Range(0, count).Select(index => new ImageRecord
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Name = index.ToString("D2")
        }).ToList();

    internal static WorkspaceSession CreateSession(TestVault testVault, List<ImageRecord> imageRecords)
    {
        var home = new WorkspaceSession(GalleryCapabilityTestSetup.Workspace(testVault.Scope), new(), new(testVault.Scope.Collections),
            new NoOpJsRuntime(), NullLogger.Instance, action => action(), () => { });
        ComponentTestAccess.Set(home, "Catalog.Images", imageRecords);
        ComponentTestAccess.Set(home, "Catalog.ImagesById", imageRecords.ToDictionary(imageRecord => imageRecord.Id));
        return home;
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? arguments) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? arguments) =>
            InvokeAsync<TValue>(identifier, arguments);
    }
}
