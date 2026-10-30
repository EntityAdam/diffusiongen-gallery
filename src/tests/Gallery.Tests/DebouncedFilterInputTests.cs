using Gallery.Web.Components;
using Microsoft.AspNetCore.Components;

namespace Gallery.Tests;

public sealed class DebouncedFilterInputTests
{
    [Fact]
    public async Task Parent_echo_acknowledges_input_without_invalidating_the_next_browser_edit()
    {
        var values = new List<string>();
        await using var input = new TestInput();
        input.Bind("", value => values.Add(value));
        Assert.True(await input.CommitAsync("c", 0));
        input.Bind("c", value => values.Add(value));
        Assert.True(await input.CommitAsync("city", 0));
        Assert.Equal(new[] { "c", "city" }, values);
    }

    [Fact]
    public async Task Reset_invalidates_pending_browser_input_even_when_the_bound_value_is_unchanged()
    {
        var values = new List<string>();
        await using var input = new TestInput();
        input.Bind("", value => values.Add(value));
        input.Bind("", value => values.Add(value), resetVersion: 1);
        Assert.False(await input.CommitAsync("city", 0));
        Assert.Empty(values);
        Assert.True(await input.CommitAsync("forest", 1));
        Assert.Equal(new[] { "forest" }, values);
    }

    [Fact]
    public async Task External_value_changes_and_disposal_reject_older_browser_submissions()
    {
        var values = new List<string>();
        var input = new TestInput();
        input.Bind("city", value => values.Add(value));
        input.Bind("forest", value => values.Add(value));
        Assert.False(await input.CommitAsync("cities", 0));
        Assert.True(await input.CommitAsync("forests", 1));
        await input.DisposeAsync();
        Assert.False(await input.CommitAsync("mountains", 1));
        Assert.Equal(new[] { "forests" }, values);
    }

    private sealed class TestInput : DebouncedFilterInput
    {
        public void Bind(string value, Action<string> changed, long resetVersion = 0)
        {
            Value = value;
            ResetVersion = resetVersion;
            ValueChanged = EventCallback.Factory.Create(new object(), changed);
            OnParametersSet();
        }
    }
}
