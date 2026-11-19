using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;
using Gallery.Web.Composition;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Gallery.Tests;

internal static class GalleryCapabilityTestSetup
{
    public static IServiceCollection AddGalleryCapabilities(this IServiceCollection services, GalleryScope gallery)
    {
        services.AddSingleton(gallery);
        GalleryServiceRegistration.RegisterCapabilities(services);
        return services;
    }

    public static WorkspaceGalleryServices Workspace(GalleryScope gallery) =>
        new(gallery.Vault, gallery.Settings, gallery.Catalog, gallery.Collections, gallery.Tags,
            gallery.Media, gallery.MediaMaintenance, gallery.Records, gallery.Duplicates, gallery.Vision);

    public static void InjectGalleryCapabilities(IComponent component, GalleryScope gallery)
    {
        using var services = new ServiceCollection().AddGalleryCapabilities(gallery).BuildServiceProvider();
        foreach (var property in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (property.IsDefined(typeof(InjectAttribute)) && property.PropertyType.Assembly == typeof(GalleryScope).Assembly)
                property.SetValue(component, services.GetRequiredService(property.PropertyType));
        }
    }
}
