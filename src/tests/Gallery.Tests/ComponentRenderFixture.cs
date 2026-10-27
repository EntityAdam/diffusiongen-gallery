using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ShadCn.Blazor.Components;
using Gallery.Core.Services;

namespace Gallery.Tests;

// Arrangement owns rendering dependencies; tests invoke the component render operation themselves.
internal sealed class ComponentRenderFixture : IAsyncDisposable
{
    private readonly ServiceProvider serviceProvider;
    public HtmlRenderer HtmlRenderer { get; }

    private ComponentRenderFixture(ServiceProvider serviceProvider, HtmlRenderer htmlRenderer)
    {
        this.serviceProvider = serviceProvider;
        HtmlRenderer = htmlRenderer;
    }

    public static ComponentRenderFixture Create(GalleryService? galleryService = null)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddShadCnBlazorComponents();
        serviceCollection.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        if (galleryService is not null) serviceCollection.AddSingleton(galleryService);
        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        try
        {
            var htmlRenderer = new HtmlRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
            return new ComponentRenderFixture(serviceProvider, htmlRenderer);
        }
        catch
        {
            serviceProvider.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await HtmlRenderer.DisposeAsync(); }
        finally { await serviceProvider.DisposeAsync(); }
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
