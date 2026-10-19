using Gallery.Web.Composition;
using Gallery.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainCompositionTests
{
    [Fact]
    public void DomainRegistration_WhenInspected_ThenExposesOnlyTheEntryManager()
    {
        // Arrange
        using var fixture = new TestVault();
        var services = new ServiceCollection();
        var expectedDomainTypes = new[] { typeof(GalleryService) };

        // Act
        GalleryServiceRegistration.Register(services, fixture.Configuration, fixture.Root);
        var registeredDomainTypes = services
            .Where(descriptor => descriptor.ServiceType.Assembly == typeof(GalleryService).Assembly)
            .Select(descriptor => descriptor.ServiceType).ToArray();

        // Assert
        Assert.Equal(expectedDomainTypes, registeredDomainTypes);
    }

    [Fact]
    public async Task DomainScope_WhenCheckedOut_ThenAnotherScopeMustWaitUntilItIsClosed()
    {
        // Arrange
        using var fixture = new TestVault();
        var services = new ServiceCollection();
        GalleryServiceRegistration.Register(services, fixture.Configuration, fixture.Root);
        using var provider = services.BuildServiceProvider();
        var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<GalleryService>();
        var second = secondScope.ServiceProvider.GetRequiredService<GalleryService>();
        await first.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await Assert.ThrowsAsync<IOException>(() => second.UnlockWithPassphraseAsync(TestVault.Passphrase));

        // Act
        firstScope.Dispose();
        await second.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var firstIsUnlocked = first.IsUnlocked;
        var secondIsUnlocked = second.IsUnlocked;

        // Assert
        Assert.False(firstIsUnlocked);
        Assert.True(secondIsUnlocked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("                                                                                 ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task SavedViewName_WhenInvalidAndVaultIsLocked_ThenRejectsTheNameBeforeStorageAccess(string name)
    {
        // Arrange
        using var fixture = new TestVault();
        // Act
        var invalidName = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveViewAsync(name, new()));

        // Assert
        AsserterExceptionContract.AssertMessageContains(
            ["Saved view name", $"'{name.Trim()}'", "1 to 80 characters"], invalidName.Message);
    }

    [Fact]
    public void DomainConstruction_WhenScopeIsDisposed_ThenOwnedTransportIsDisposed()
    {
        using var fixture = new TestVault();
        var transport = new DisposalTrackingTransport();
        var client = new HttpClient(transport);
        var options = new Gallery.Core.Models.GalleryRuntimeOptions { StateDirectory = fixture.Root + "-state" };

        var service = GalleryService.Create(fixture.Root, options, client);
        service.Dispose();

        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void DomainConstruction_WhenConstructionFails_ThenOwnedTransportIsDisposed()
    {
        using var fixture = new TestVault();
        var transport = new DisposalTrackingTransport();
        var client = new HttpClient(transport);
        var options = new Gallery.Core.Models.GalleryRuntimeOptions { StateDirectory = fixture.Root + "-state" };

        Assert.Throws<ArgumentException>(() => GalleryService.Create("invalid\0path", options, client));

        Assert.True(transport.IsDisposed);
    }

    private sealed class DisposalTrackingTransport : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Transport lifetime checks do not send requests.");

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }

}
