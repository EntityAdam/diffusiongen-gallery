using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;
using Gallery.Web.Composition;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Gallery.Tests;

internal static class GalleryCapabilityTestSetup
{
    public static IServiceCollection AddGalleryCapabilities(this IServiceCollection services, GalleryService gallery) =>
        services.AddGalleryCapabilities(gallery.Scope);

    public static IServiceCollection AddGalleryCapabilities(this IServiceCollection services, GalleryScope gallery)
    {
        services.AddSingleton(gallery);
        GalleryServiceRegistration.RegisterCapabilities(services);
        return services;
    }

    public static WorkspaceGalleryServices Workspace(GalleryService gallery) =>
        new(gallery.Scope.Vault, gallery.Scope.Settings, gallery.Scope.Catalog, gallery.Scope.Collections, gallery.Scope.Tags,
            gallery.Scope.Media, gallery.Scope.MediaMaintenance, gallery.Scope.Records, gallery.Scope.Duplicates);

    public static void InjectGalleryCapabilities(IComponent component, GalleryService gallery)
    {
        using var services = new ServiceCollection().AddGalleryCapabilities(gallery).BuildServiceProvider();
        foreach (var property in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (property.IsDefined(typeof(InjectAttribute)) && property.PropertyType.Assembly == typeof(GalleryService).Assembly)
                property.SetValue(component, services.GetRequiredService(property.PropertyType));
        }
    }
}
