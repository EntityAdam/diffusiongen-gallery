using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadCn.Blazor.Components;

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

    public static ComponentRenderFixture Create()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddShadCnBlazorComponents();
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
}
