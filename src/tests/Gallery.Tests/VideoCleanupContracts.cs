using System.Diagnostics;
using System.Reflection;
using Gallery.Web.Managers.Gateways;

namespace Gallery.Tests;

// Scoped class admission: an unassociated process deterministically faults both execution and cleanup.
// Public video operations always start a process, so this narrow lifecycle failure cannot be arranged there.
[Trait("Category", "class_test")]
public static class VideoCleanupContracts
{
    [Fact]
    public static async Task ProcessExecution_WhenExecutionAndCleanupBothFail_ThenPreservesBothCauses()
    {
        // Arrange
        using var process = new Process();
        var execute = typeof(VideoTools).GetMethod("ExecuteProcessAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        AggregateException? failure = null;

        // Act
        try
        {
            await (Task<byte[]>)execute.Invoke(null, [process, "test-tool", CancellationToken.None])!;
        }
        catch (AggregateException exception) { failure = exception; }

        // Assert
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Contains("test-tool", aggregate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed", aggregate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(aggregate.InnerExceptions,
            primary => Assert.IsType<InvalidOperationException>(primary),
            cleanup => Assert.IsType<InvalidOperationException>(cleanup));
        Assert.Contains("StandardOut", aggregate.InnerExceptions[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process", aggregate.InnerExceptions[1].Message, StringComparison.OrdinalIgnoreCase);
    }
}
