using Gallery.Tests;
using Gallery.Web.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Gallery.BrowserTests;

public sealed class GalleryKeyboardTests
{
    [Fact]
    public async Task X_then_T_edits_only_the_selected_card()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        foreach (var (name, width) in new[] { ("selected.png", 4), ("other.png", 8) })
        {
            using var image = new Image<Rgba32>(width, 4);
            using var png = new MemoryStream();
            image.SaveAsPng(png);
            png.Position = 0;
            var record = await vault.Scope.Ingestion.ImportAsync(png, name, "folder");
            await vault.Scope.Records.UpdateAsync(record.Id, edit => edit with { Tags = "portrait" });
        }
        vault.Scope.Vault.LockVault();

        await using var application = new WebApplicationFactory<App>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(vault.Scope)));
        application.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = application.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(client.BaseAddress!, "gallery").AbsoluteUri);
        await page.GetByLabel("Passphrase", new() { Exact = true }).FillAsync(TestVault.Passphrase);
        await page.GetByRole(AriaRole.Button, new() { Name = "Unlock gallery" }).ClickAsync();
        var select = page.GetByRole(AriaRole.Button, new() { Name = "Select selected", Exact = true });
        await Expect(select).ToBeEnabledAsync();
        await select.HoverAsync();
        await select.PressAsync("x");
        await Expect(select).ToHaveAttributeAsync("aria-pressed", "true");
        await select.PressAsync("t");
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Edit tags" });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Remove tag portrait" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save changes" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        var saved = (await vault.Scope.Catalog.ListAsync()).ToDictionary(record => record.OriginalName);
        Assert.Empty(saved["selected.png"].Tags);
        Assert.Equal("portrait", saved["other.png"].Tags);
    }
}
