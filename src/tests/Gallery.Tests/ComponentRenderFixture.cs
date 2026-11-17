using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Gallery.Web.Components.Pages;
using Gallery.Web.Components.Workspace;
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

    public T LatestComponent<T>() where T : IComponent =>
        serviceProvider.GetRequiredService<ComponentActivator>().Components.OfType<T>().Last();

    public async Task<(GalleryWorkspace Component, HtmlRootComponent Root)> RenderWorkspaceAsync(string? route = null)
    {
        var root = await HtmlRenderer.Dispatcher.InvokeAsync(() => HtmlRenderer.RenderComponentAsync<Home>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Home.Route)] = route })));
        return (LatestComponent<GalleryWorkspace>(), root);
    }

    private ComponentRenderFixture(ServiceProvider serviceProvider, HtmlRenderer htmlRenderer)
    {
        this.serviceProvider = serviceProvider;
        HtmlRenderer = htmlRenderer;
    }

    public static ComponentRenderFixture Create(GalleryService? galleryService = null, IJSRuntime? jsRuntime = null)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddSingleton<ComponentActivator>();
        serviceCollection.AddSingleton<IComponentActivator>(provider => provider.GetRequiredService<ComponentActivator>());
        serviceCollection.AddShadCnBlazorComponents();
        serviceCollection.AddSingleton<IJSRuntime>(jsRuntime ?? new NoOpJsRuntime());
        serviceCollection.AddScoped<Gallery.Web.Components.Workspace.OrganizationHistory>();
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

    private sealed class ComponentActivator(IServiceProvider services) : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)ActivatorUtilities.CreateInstance(services, componentType);
            Components.Add(component);
            return component;
        }
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
