using System.Net;
using System.Text.RegularExpressions;
using Gallery.Components;
using Gallery.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadCn.Blazor.Components;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class UiComponentTests
{
    [Fact]
    public async Task ImageCard_WhenSelectedAndRated_ThenRetainsKeyboardHooksAndAccessibleLibraryControls()
    {
        var html = await RenderAsync<GalleryImageCard>(new()
        {
            [nameof(GalleryImageCard.Image)] = new ImageRecord { Id = "sample", Name = "Sample", Rating = 3, Favorite = true },
            [nameof(GalleryImageCard.ShowSelection)] = true,
            [nameof(GalleryImageCard.Selected)] = true
        });

        Assert.Contains("image-card", html);
        Assert.Contains("data-image-id=\"sample\"", html);
        Assert.Contains("image-open", html);
        Assert.Contains("aria-label=\"Select Sample\"", html);
        Assert.Contains("aria-label=\"Rating for Sample: 3 out of 5 stars\"", html);
        Assert.Contains("aria-label=\"Clear rating\"", html);
        Assert.Contains("h-7!", html);
        Assert.Contains("focus-visible:ring-ring", html);
        Assert.Contains("<svg", html);
        Assert.DoesNotContain("<Trash", html);
    }

    [Theory]
    [InlineData(false, "input", "password")]
    [InlineData(true, "textarea", null)]
    public async Task Credentials_WhenModeChanges_ThenPreservesSecretFieldAndTabSemantics(
        bool recovery, string field, string? type)
    {
        var html = await RenderAsync<GalleryCredentialInput>(new()
        {
            [nameof(GalleryCredentialInput.Value)] = "secret value",
            [nameof(GalleryCredentialInput.UseRecovery)] = recovery,
            [nameof(GalleryCredentialInput.Disabled)] = true
        });

        Assert.Contains("role=\"tablist\"", html);
        Assert.Equal(2, Regex.Matches(html, "role=\"tab\"").Count);
        Assert.Contains($"<{field}", html);
        if (type is not null) Assert.Contains($"type=\"{type}\"", html);
        Assert.Contains("autocomplete=\"off\"", html);
        Assert.Contains("secret value", html);
        Assert.Equal(3, Regex.Matches(html, @"\bdisabled(?:=|\s|>)").Count);
    }

    [Fact]
    public async Task Filters_WhenBusy_ThenEveryFieldIsDisabledIncludingTypedNativeFields()
    {
        var html = await RenderAsync<GalleryFilterPanel>(new()
        {
            [nameof(GalleryFilterPanel.Filter)] = new GalleryFilter(),
            [nameof(GalleryFilterPanel.Disabled)] = true
        });

        var controls = Regex.Matches(html, @"<(?:input|select|button)\b[^>]*>");
        Assert.NotEmpty(controls);
        foreach (Match control in controls) Assert.Contains("disabled", control.Value);
        Assert.Contains("type=\"date\"", html);
        Assert.Contains("type=\"number\"", html);
        Assert.Contains("role=\"checkbox\"", html);
    }

    [Fact]
    public async Task BackupDestination_WhenAzureSelected_ThenPreservesPasswordAndSavedCredentialHint()
    {
        var html = await RenderAsync<BackupLocation>(new()
        {
            [nameof(BackupLocation.Type)] = "azure",
            [nameof(BackupLocation.Container)] = "backups",
            [nameof(BackupLocation.HasSavedCredentials)] = true
        });

        Assert.Contains("value=\"backups\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("Saved securely; leave blank to keep it", html);
        Assert.Contains("autocomplete=\"off\"", html);
    }

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddShadCnBlazorComponents();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
