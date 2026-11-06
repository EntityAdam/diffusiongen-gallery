using System.Net;
using System.Reflection;
using Gallery.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class AdvancedSelectTests
{
    [Fact]
    public async Task SingleSelection_SearchesCreatesClearsAndClosesOnSelection()
    {
        await using var fixture = ComponentRenderFixture.Create();
        AdvancedSelect? select = null;
        IReadOnlyList<string> selected = [];
        var root = await Render(fixture, false, false, false, value => select = value, values => selected = values);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("focus"));
        await Search(fixture, select!, "de");
        Assert.Contains(">Design</span>", await Html(fixture, root));
        Assert.DoesNotContain(">Marketing</span>", await Html(fixture, root));
        Assert.Contains("value=\"\"", await Html(fixture, root));
        Assert.DoesNotContain("value=\"de\"", await Html(fixture, root));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("enter"));
        Assert.Equal(new[] { "Design" }, selected);
        Assert.Contains("aria-expanded=\"false\"", await Html(fixture, root));

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("focus"));
        await Search(fixture, select!, "Research");
        Assert.Contains("Create “Research”", await Html(fixture, root));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("enter"));
        Assert.Equal(new[] { "Research" }, selected);
        await Invoke(fixture, select!, "ClearAsync");
        Assert.Empty(selected);
    }

    [Fact]
    public async Task MultipleSelection_TogglesExistingValuesCreatesWithoutDuplicatesAndKeepsMenuOpen()
    {
        await using var fixture = ComponentRenderFixture.Create();
        AdvancedSelect? select = null;
        IReadOnlyList<string> selected = [];
        var root = await Render(fixture, true, false, false, value => select = value, values => selected = values);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("focus"));
        await Search(fixture, select!, "design");
        Assert.DoesNotContain("Create “design”", await Html(fixture, root));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("enter"));
        Assert.Equal(new[] { "Design" }, selected);
        Assert.Contains("aria-label=\"Remove tag Design\"", await Html(fixture, root));
        Assert.Contains("aria-expanded=\"true\"", await Html(fixture, root));
        await Search(fixture, select!, "Research");
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("enter"));
        Assert.Equal(new[] { "Design", "Research" }, selected);
        await Search(fixture, select!, "DESIGN");
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("enter"));
        Assert.Equal(new[] { "Research" }, selected);
        await Invoke(fixture, select!, "RemoveAsync", "Research");
        Assert.Empty(selected);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("escape"));
        Assert.Contains("aria-expanded=\"false\"", await Html(fixture, root));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RestrictedSelection_DoesNotPublishDisabledActionsOrCreateUnknownFilterValues(bool disabled, bool filter)
    {
        await using var fixture = ComponentRenderFixture.Create();
        AdvancedSelect? select = null;
        var changes = 0;
        var root = await Render(fixture, false, disabled, filter, value => select = value, _ => changes++);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("focus"));
        await Search(fixture, select!, "Unknown");
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => select!.HandleKey("enter"));
        if (disabled) await Invoke(fixture, select!, "ChooseAsync", "Design");
        Assert.Equal(0, changes);
        if (filter) Assert.DoesNotContain("Create “Unknown”", await Html(fixture, root));
    }

    [Fact]
    public async Task QuickTags_RemovesExistingTagsImmediatelyAndSavesOnlyNewSelections()
    {
        await using var fixture = ComponentRenderFixture.Create();
        QuickOrganizationDialog? dialog = null;
        var removed = new List<string>();
        string? saved = null;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
            fixture.HtmlRenderer.RenderComponentAsync<QuickTagTestHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(QuickTagTestHost.Capture)] = (Action<QuickOrganizationDialog>)(value => dialog = value),
                [nameof(QuickTagTestHost.Removed)] = (Action<string>)(value => removed.Add(value)),
                [nameof(QuickTagTestHost.Saved)] = (Action<string>)(value => saved = value)
            })));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(QuickOrganizationDialog).GetMethod("TagsChangedAsync", flags)!
                .Invoke(dialog, [new[] { "Product", "Research" }])!;
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(dialog, null);
        });
        Assert.Equal(new[] { "Design" }, removed);
        Assert.Null(saved);
        string html = await Html(fixture, root);
        Assert.DoesNotContain("aria-label=\"Remove tag Design\"", html);
        Assert.Contains("aria-label=\"Remove tag Product\"", html);
        Assert.Contains("aria-label=\"Remove tag Research\"", html);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
            await (Task)typeof(QuickOrganizationDialog).GetMethod("SaveAsync", flags)!.Invoke(dialog, null)!);
        Assert.Equal("Research", saved);
    }

    private sealed class QuickTagTestHost : ComponentBase
    {
        [Parameter] public Action<QuickOrganizationDialog>? Capture { get; set; }
        [Parameter] public Action<string>? Removed { get; set; }
        [Parameter] public Action<string>? Saved { get; set; }
        private string tags = "Design, Product";
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<QuickOrganizationDialog>(0);
            builder.AddAttribute(1, nameof(QuickOrganizationDialog.Tags), tags);
            builder.AddAttribute(2, nameof(QuickOrganizationDialog.Options), new[] { "Design", "Product" });
            builder.AddAttribute(3, nameof(QuickOrganizationDialog.RemoveTag), EventCallback.Factory.Create<string>(this, removed =>
            {
                tags = string.Join(", ", tags.Split(", ").Where(tag => tag != removed));
                Removed?.Invoke(removed);
            }));
            builder.AddAttribute(4, nameof(QuickOrganizationDialog.Save), EventCallback.Factory.Create<string>(this, value => Saved?.Invoke(value)));
            builder.AddComponentReferenceCapture(5, instance => Capture?.Invoke((QuickOrganizationDialog)instance));
            builder.CloseComponent();
        }
    }

    private static Task<HtmlRootComponent> Render(ComponentRenderFixture fixture, bool multiple, bool disabled, bool filter,
        Action<AdvancedSelect> capture, Action<IReadOnlyList<string>> changed) => fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
        fixture.HtmlRenderer.RenderComponentAsync<TestHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(TestHost.Multiple)] = multiple, [nameof(TestHost.Disabled)] = disabled,
            [nameof(TestHost.AllowCreate)] = !filter, [nameof(TestHost.Capture)] = capture, [nameof(TestHost.Changed)] = changed
        })));

    private static Task<string> Html(ComponentRenderFixture fixture, HtmlRootComponent root) =>
        fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));

    private static Task Search(ComponentRenderFixture fixture, AdvancedSelect select, string query) => Invoke(fixture, select, "Search", new ChangeEventArgs { Value = query });

    private static Task Invoke(ComponentRenderFixture fixture, AdvancedSelect select, string method, params object[] args) =>
        fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var result = typeof(AdvancedSelect).GetMethod(method, flags)!.Invoke(select, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(select, null);
        });

    private sealed class TestHost : ComponentBase
    {
        [Parameter] public bool Multiple { get; set; }
        [Parameter] public bool Disabled { get; set; }
        [Parameter] public bool AllowCreate { get; set; }
        [Parameter] public Action<AdvancedSelect>? Capture { get; set; }
        [Parameter] public Action<IReadOnlyList<string>>? Changed { get; set; }
        private string value = "";
        private IReadOnlyList<string> values = [];
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AdvancedSelect>(0);
            builder.AddAttribute(1, nameof(AdvancedSelect.Options), new[] { "Design", "DESIGN", "Development", "Marketing" });
            builder.AddAttribute(2, nameof(AdvancedSelect.Multiple), Multiple);
            builder.AddAttribute(3, nameof(AdvancedSelect.Disabled), Disabled);
            builder.AddAttribute(4, nameof(AdvancedSelect.AllowCreate), AllowCreate);
            builder.AddAttribute(5, nameof(AdvancedSelect.Label), "Tags");
            builder.AddAttribute(6, nameof(AdvancedSelect.ItemLabel), "tag");
            builder.AddAttribute(7, nameof(AdvancedSelect.Value), value);
            builder.AddAttribute(8, nameof(AdvancedSelect.Values), values);
            builder.AddAttribute(9, nameof(AdvancedSelect.ValueChanged), EventCallback.Factory.Create<string>(this, updated =>
            { value = updated; Changed?.Invoke(string.IsNullOrEmpty(updated) ? [] : new[] { updated }); }));
            builder.AddAttribute(10, nameof(AdvancedSelect.ValuesChanged), EventCallback.Factory.Create<IReadOnlyList<string>>(this, updated =>
            { values = updated; Changed?.Invoke(updated); }));
            builder.AddComponentReferenceCapture(11, instance => Capture?.Invoke((AdvancedSelect)instance));
            builder.CloseComponent();
        }
    }
}
