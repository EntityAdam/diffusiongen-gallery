using AngleSharp.Dom;
using Bunit;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Browsing;
using Gallery.Web.Components.Features.Collections;
using Gallery.Web.Components.Features.Media;
using Gallery.Web.Components.Pages;
using Gallery.Web.Components.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShadCn.Blazor.Components;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class GalleryBrowsingInteractionTests
{
    [Fact]
    public async Task Card_controls_update_selection_rating_and_favorite()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await using var ui = CreateContext(vault);
        var gallery = await RenderGalleryAsync(ui);

        await gallery.Find("button[aria-label='Select sample']").ClickAsync();
        Assert.Equal("true", gallery.Find("button[aria-label='Select sample']").GetAttribute("aria-pressed"));
        await gallery.Find("button[aria-label='Rate 3 out of 5 stars']").ClickAsync();
        Assert.NotNull(gallery.Find("[role=group][aria-label='Rating for sample: 3 out of 5 stars']"));
        await gallery.Find("button[aria-label='Favorite']").ClickAsync();
        Assert.Equal("true", gallery.Find("button[aria-label='Remove favorite']").GetAttribute("aria-pressed"));
        await gallery.Find("button[aria-label='Clear rating']").ClickAsync();
        var saved = Assert.Single(await vault.Scope.Catalog.ListAsync());
        Assert.Equal(0, saved.Rating);
        Assert.True(saved.IsFavorite);
    }

    [Theory]
    [InlineData(false, false, false, false, "Not analyzed")]
    [InlineData(false, true, false, true, "Vision analysis accepted")]
    [InlineData(true, false, true, false, "Vision analysis awaiting acceptance")]
    [InlineData(true, true, true, true, "Vision analysis awaiting acceptance")]
    public async Task Card_shows_media_summary_and_accessible_statuses(bool video, bool analyzed, bool pending, bool reviewed, string vision)
    {
        await using var ui = CreateContext();
        var filename = video ? "original.mp4" : "original.png";
        var card = ui.Render<GalleryImageCard>(p => p.Add(c => c.Image, new ImageRecord
        {
            Name = "display name", OriginalName = filename, Width = 1536, Height = 1024, OriginalBytes = 2516582,
            MediaType = video ? "video" : "image", DurationSeconds = 3665, IsReviewed = reviewed,
            Description = analyzed ? "vision result" : "",
            Generation = analyzed ? new() { Model = @"models\cat.safetensors", Prompt = "a cat in sunlight" } : null
        }).Add(c => c.HasSuggestion, pending));

        Assert.Equal(filename, card.Find("h3").TextContent);
        var text = string.Concat(card.Nodes.Select(node => node.TextContent));
        Assert.Contains("1536 × 1024", text);
        Assert.Contains(video ? "01:01:05" : "2MP", text);
        Assert.Contains("2.4 MiB", text);
        var labels = card.FindAll("[role=img]").Select(icon => icon.GetAttribute("aria-label")).ToArray();
        Assert.Contains(video ? "Video" : "Image", labels);
        Assert.Contains(vision, labels);
        Assert.Contains(reviewed ? "Reviewed" : "Not reviewed", labels);
        Assert.Equal(analyzed, labels.Contains("AI generated: ComfyUI metadata detected"));
        if (analyzed)
        {
            Assert.Contains("Model: cat", text);
            Assert.DoesNotContain("cat.safetensors", text);
            Assert.Contains("a cat in sunlight", text);
        }
    }

    [Theory]
    [InlineData("Review statuses")]
    [InlineData("Deletion statuses")]
    [InlineData("Media types")]
    [InlineData("Orientations")]
    [InlineData("Analysis statuses")]
    [InlineData("Ratings")]
    [InlineData("Tags")]
    public async Task Clear_filter_changes_only_the_chosen_criterion(string label)
    {
        await using var ui = CreateContext();
        var filter = new GalleryFilter
        {
            Review = "reviewed", Deletion = "marked", Media = "video", Orientation = "portrait",
            Analysis = "analyzed", MinRating = 4, Tag = "cat, dog", Vision = new() { Subject = "cat" }
        };
        GalleryFilter? changed = null;
        var panel = ui.Render<GalleryFilterPanel>(p => p.Add(c => c.Filter, filter)
            .Add(c => c.Changed, (GalleryFilter update) => changed = update));
        if (label == "Tags")
        {
            await panel.Find("button[aria-label='Remove tag cat']").ClickAsync();
            Assert.Equal(filter with { Tag = "dog" }, changed);
        }
        await panel.Find($"button[aria-label='Clear {label}']").ClickAsync();
        Assert.Equal(label switch
        {
            "Review statuses" => filter with { Review = GalleryFilter.AnyValue },
            "Deletion statuses" => filter with { Deletion = GalleryFilter.AnyValue },
            "Media types" => filter with { Media = GalleryFilter.AnyValue },
            "Orientations" => filter with { Orientation = GalleryFilter.AnyValue },
            "Analysis statuses" => filter with { Analysis = GalleryFilter.AnyValue },
            "Ratings" => filter with { MinRating = 0 },
            "Tags" => filter with { Tag = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(label))
        }, changed);
    }

    [Fact]
    public async Task Initial_cards_remain_browsable_while_the_next_page_loads()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = BrowsingWorkspaceFixture.CreateImages(15).Select((r, i) =>
            r with { ImportedAt = DateTimeOffset.UnixEpoch.AddDays(-i) }).ToList();
        await using var ui = CreateContext(vault);
        ui.Services.AddSingleton<IGalleryCatalog>(new BrowsingWorkspaceFixture.BrowsingCatalogSource(vault.Scope.Catalog, records, release.Task));
        ui.Services.AddSingleton<IGalleryMedia>(new BrowsingWorkspaceFixture.ThumbnailMedia());
        var gallery = await RenderGalleryAsync(ui);
        Assert.Contains("initial page", gallery.Find("section[aria-label='Gallery'] [role=status]").TextContent);
        string[] Filenames() => gallery.FindAll("section[aria-label='Gallery'] h3").Select(e => e.TextContent).ToArray();
        IElement Next() => gallery.FindAll("button").Single(e => e.TextContent.Trim() == "Next");
        try
        {
            Assert.Equal(records.Take(12).Select(r => r.Name), Filenames());
            Assert.True(Next().HasAttribute("disabled"));
        }
        finally { release.TrySetResult(); }

        await gallery.WaitForAssertionAsync(() => Assert.False(Next().HasAttribute("disabled")));
        Assert.Empty(gallery.FindAll("section[aria-label='Gallery'] [role=status]"));
        await Next().ClickAsync();
        Assert.Equal(records.Skip(12).Select(r => r.Name), Filenames());
    }

    [Fact]
    public async Task Sidebar_lists_workflows_in_navigation_order()
    {
        await using var ui = CreateContext();
        var nav = ui.Render<WorkspaceNavigation>(p => p.Add(c => c.ItemDisabled, _ => false));
        Assert.Equal(["Gallery", "Change view", "Filters", "Organize", "Review", "AI analysis", "Collections",
            "Tags", "Duplicates", "Import media", "Trash"],
            nav.FindAll("nav :is(a,button)").Select(e => e.GetAttribute("aria-label")));
    }

    [Theory]
    [InlineData("Change view", "Change view")]
    [InlineData("Filters", "Filters")]
    [InlineData("Review", "Guided review")]
    [InlineData("AI analysis", "AI analysis")]
    [InlineData("Organize", "Organize")]
    public async Task Sidebar_tool_toggles_its_drawer(string label, string title)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var ui = CreateContext(vault);
        var gallery = await RenderGalleryAsync(ui);
        var selector = $"nav button[aria-label='{label}']";
        await gallery.Find(selector).ClickAsync();
        Assert.Equal(title, gallery.Find("aside:not([hidden]) h2").TextContent);
        Assert.Equal("page", gallery.Find(selector).GetAttribute("aria-current"));
        await gallery.Find(selector).ClickAsync();
        Assert.Empty(gallery.FindAll("aside:not([hidden]) h2"));
        Assert.Equal("page", gallery.Find("nav [aria-label='Gallery']").GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Trash_selection_enables_restore_and_delete()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var record = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "marked.png", "folder");
        await vault.Scope.Records.UpdateAsync(record.Id, edit => edit with { IsMarkedForDeletion = true });
        await using var ui = CreateContext(vault);
        var gallery = await RenderGalleryAsync(ui);
        await gallery.Find("nav button[aria-label=Trash]").ClickAsync();
        IElement TrashAction(string name) => gallery.FindAll("section[aria-label=Trash] button").Single(b => b.TextContent.Trim() == name);
        Assert.True(TrashAction("Delete Forever").HasAttribute("disabled"));
        Assert.True(TrashAction("Restore").HasAttribute("disabled"));
        await gallery.Find("button[aria-label='Select marked']").ClickAsync();
        Assert.False(TrashAction("Delete Forever").HasAttribute("disabled"));
        Assert.False(TrashAction("Restore").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Organize_and_analysis_keep_the_selected_media_when_switching_tools()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await vault.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "portrait" });
        await using var ui = CreateContext(vault);
        var gallery = await RenderGalleryAsync(ui);
        await gallery.Find("nav button[aria-label=Organize]").ClickAsync();
        await gallery.Find("button[aria-label='Select sample']").ClickAsync();
        await gallery.Find("nav button[aria-label='AI analysis']").ClickAsync();
        var analyze = Assert.Single(gallery.FindAll("aside button"), b => b.TextContent.Trim() == "Analyze selected (1)");
        Assert.False(analyze.HasAttribute("disabled"));
        await gallery.Find("nav button[aria-label=Organize]").ClickAsync();
        Assert.NotNull(gallery.Find("aside button[aria-label='Remove tag portrait']"));
    }

    [Fact]
    public async Task Organize_stays_open_with_updated_tags_after_save()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var record = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await vault.Scope.Records.UpdateAsync(record.Id, edit => edit with { Tags = "cat, dog" });
        await using var ui = CreateContext(vault);
        var gallery = await RenderGalleryAsync(ui);
        await gallery.Find("button[aria-label='Select sample']").ClickAsync();
        await gallery.Find("nav button[aria-label=Organize]").ClickAsync();
        await gallery.Find("button[aria-label='Remove tag cat']").ClickAsync();
        await gallery.Find("aside button[type=submit]").ClickAsync();
        Assert.Equal("dog", Assert.Single(await vault.Scope.Catalog.ListAsync()).Tags);
        Assert.NotNull(gallery.Find("aside button[aria-label='Remove tag dog']"));
        Assert.Empty(gallery.FindAll("aside button[aria-label='Remove tag cat']"));
        Assert.True(gallery.Find("aside button[type=submit]").HasAttribute("disabled"));
    }

    private static async Task<IRenderedComponent<Home>> RenderGalleryAsync(BunitContext ui)
    {
        var gallery = ui.Render<Home>();
        await gallery.WaitForElementAsync("section[aria-label='Gallery']", TimeSpan.FromSeconds(10));
        return gallery;
    }

    private static BunitContext CreateContext(TestVault? vault = null)
    {
        var ui = new BunitContext();
        ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        ui.Services.AddShadCnBlazorComponents();
        ui.Services.AddScoped<OrganizationHistory>();
        if (vault is not null) ui.Services.AddGalleryCapabilities(vault.Scope);
        return ui;
    }
}
