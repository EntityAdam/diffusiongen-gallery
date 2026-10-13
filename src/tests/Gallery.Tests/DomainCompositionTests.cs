using Gallery.Services;
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
        GalleryService.Register(services, fixture.Configuration, fixture.Root);
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
        GalleryService.Register(services, fixture.Configuration, fixture.Root);
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
        var expectedException = new ExpectedException("invalid saved view name is rejected at the manager boundary", "Saved view name", "1", "80");

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.Service.SaveViewAsync(name, new()));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(expectedException, exception);
    }
}
