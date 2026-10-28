// Class tests pin DI scope ownership, startup snapshots and transport policy, which HTTP cannot observe.
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Web.Composition;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Validators;
using Microsoft.Extensions.DependencyInjection;
using ConfigurationProvider = Gallery.Core.Services.ConfigurationProviders.ConfigurationProvider;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainCompositionTests
{
    [Fact]
    public async Task DomainRegistration_WhenConfigurationChanges_ThenKeepsTheRegisteredStateDirectory()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        var expectedStateDirectory = testVault.Root + "-state";
        var changedStateDirectory = Path.Combine(expectedStateDirectory, "changed");
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);
        testVault.Configuration["Gallery:StateDirectory"] = changedStateDirectory;
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var serviceScope = serviceProvider.CreateScope();
        var galleryService = serviceScope.ServiceProvider.GetRequiredService<GalleryService>();
        var expectedScenario = ExpectedScenario.CreateFrom("Scopes use the state directory captured at registration, not later configuration changes.",
            new { VaultRoot = testVault.Root, ChangedDirectoryExists = false });

        // Act
        await galleryService.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);

        // Assert - gather persisted state
        var actualVaultRoot = JsonSerializer.Deserialize<string>(
            File.ReadAllText(Path.Combine(expectedStateDirectory, "last-vault.json")));
        var actualChangedDirectoryExists = Directory.Exists(changedStateDirectory);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { VaultRoot = actualVaultRoot, ChangedDirectoryExists = actualChangedDirectoryExists });
    }

    [Fact]
    public void VisionClient_WhenCreated_ThenHasNoRequestTimeout()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var expectedTimeout = Timeout.InfiniteTimeSpan;

        // Act
        using var actualHttpClient = httpClientFactory.CreateClient("gallery-vision");

        // Assert
        Assert.Equal(expectedTimeout, actualHttpClient.Timeout);
    }

    [Fact]
    public void VisionHandler_WhenCreated_ThenDisablesRedirectsAndProxy()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        var httpMessageHandlerFactory = serviceProvider.GetRequiredService<IHttpMessageHandlerFactory>();
        var expectedScenario = ExpectedScenario.CreateFrom("Vision HTTP requests must not follow redirects or use a system proxy.", new
        {
            HandlerType = typeof(HttpClientHandler).FullName,
            AllowAutoRedirect = false,
            UseProxy = false
        });

        // Act
        var actualHttpMessageHandler = httpMessageHandlerFactory.CreateHandler("gallery-vision");

        // Assert - gather the underlying transport
        while (actualHttpMessageHandler is DelegatingHandler { InnerHandler: { } innerHandler })
            actualHttpMessageHandler = innerHandler;
        var actualHttpClientHandler = actualHttpMessageHandler as HttpClientHandler;

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario, new
        {
            HandlerType = actualHttpMessageHandler.GetType().FullName,
            AllowAutoRedirect = actualHttpClientHandler?.AllowAutoRedirect,
            UseProxy = actualHttpClientHandler?.UseProxy
        });
    }

    [Fact]
    public void DomainRegistration_WhenInspected_ThenExposesOnlyTheEntryManager()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        var expectedDomainTypes = new[] { typeof(GalleryService) };

        // Act
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);

        // Assert - gather registrations without changing them
        var actualDomainTypes = serviceCollection
            .Where(serviceDescriptor => serviceDescriptor.ServiceType.Assembly == typeof(GalleryService).Assembly)
            .Select(serviceDescriptor => serviceDescriptor.ServiceType);

        // Assert
        Assert.Equal(expectedDomainTypes, actualDomainTypes);
    }

    [Fact]
    public void DomainConfiguration_WhenItsVisibilityIsInspected_ThenTheProviderIsInternal()
    {
        // Arrange
        const bool ExpectedIsPublic = false;

        // Act
        var actualIsPublic = typeof(ConfigurationProvider).IsVisible;

        // Assert
        Assert.Equal(ExpectedIsPublic, actualIsPublic);
    }

    [Fact]
    public async Task DomainScope_WhenCheckoutIsHeld_ThenTheOwnerStaysUnlockedAndAnotherScopeCannotUnlock()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var serviceScopeOwner = serviceProvider.CreateScope();
        using var serviceScopeWaiting = serviceProvider.CreateScope();
        var galleryServiceOwner = serviceScopeOwner.ServiceProvider.GetRequiredService<GalleryService>();
        var galleryServiceWaiting = serviceScopeWaiting.ServiceProvider.GetRequiredService<GalleryService>();
        await galleryServiceOwner.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var expectedScenario = ExpectedScenario.CreateFrom("An existing checkout remains usable and excludes competing scopes.",
            new { OwnerIsUnlocked = true, WaitingIsUnlocked = false });

        // Act
        await Assert.ThrowsAsync<IOException>(() => galleryServiceWaiting.UnlockWithPassphraseAsync(TestVault.Passphrase));

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { OwnerIsUnlocked = galleryServiceOwner.IsUnlocked, WaitingIsUnlocked = galleryServiceWaiting.IsUnlocked });
    }

    [Fact]
    public async Task DomainScope_WhenThePreviousScopeHasClosed_ThenItIsLockedAndAnotherScopeCanUnlock()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);
        using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var serviceScopeClosed = serviceProvider.CreateScope();
        using var serviceScopeNext = serviceProvider.CreateScope();
        var galleryServiceClosed = serviceScopeClosed.ServiceProvider.GetRequiredService<GalleryService>();
        var galleryServiceNext = serviceScopeNext.ServiceProvider.GetRequiredService<GalleryService>();
        await galleryServiceClosed.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        serviceScopeClosed.Dispose();
        var expectedScenario = ExpectedScenario.CreateFrom("Closing a scope locks it and releases its checkout for the next scope.",
            new { ClosedIsUnlocked = false, NextIsUnlocked = true });

        // Act
        await galleryServiceNext.UnlockWithPassphraseAsync(TestVault.Passphrase);

        // Assert
        AsserterScenario.AssertObservationsMatch(expectedScenario,
            new { ClosedIsUnlocked = galleryServiceClosed.IsUnlocked, NextIsUnlocked = galleryServiceNext.IsUnlocked });
    }

    // Missing, whitespace and overlong names are rejected before accessing a locked vault.
    [Theory]
    [InlineData("")]
    [InlineData("                                                                                 ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task SavedViewName_WhenInvalidAndVaultIsLocked_ThenRejectsTheNameBeforeStorageAccess(string invalidName)
    {
        // Arrange
        using var testVault = new TestVault();
        var expectedExceptionContract = ExpectedExceptionContract.Create(typeof(InvalidSavedViewNameException),
            ["Saved view name", $"'{invalidName.Trim()}'", "1 to 80 characters"],
            HttpStatusCode.BadRequest, Severity.Error, "Saved view name is invalid.", "Gallery.ValidateSavedViewName",
            new Dictionary<string, object?>
            {
                ["SavedView.Name"] = invalidName.Trim(),
                ["SavedView.MinimumNameLength"] = 1,
                ["SavedView.MaximumNameLength"] = 80
            });

        // Act
        var actualInvalidSavedViewNameException = await Assert.ThrowsAsync<InvalidSavedViewNameException>(
            () => testVault.Service.SaveViewAsync(invalidName, new()));

        // Assert
        AsserterExceptionContract.AssertMatches(expectedExceptionContract, actualInvalidSavedViewNameException);
    }

    // Both inclusive name-length limits are accepted by the same validation rule.
    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    public void SavedViewName_WhenLengthIsAtALimit_ThenAcceptsTheName(int validNameLength)
    {
        // Arrange
        var validName = Convert.ToHexString(RandomNumberGenerator.GetBytes(validNameLength))[..validNameLength];

        // Act
        var actualException = Record.Exception(() => SavedViewNameValidator.Validate(validName));

        // Assert
        Assert.True(actualException is null,
            $"Expected the {validNameLength}-character saved-view name '{validName}' to be accepted; " +
            $"Actual: {actualException?.ToString() ?? "<no exception>"}.");
    }

    [Fact]
    public void DomainConstruction_WhenScopeIsDisposed_ThenOwnedTransportIsDisposed()
    {
        // Arrange
        using var testVault = new TestVault();
        using var disposalTrackingTransport = new DisposalTrackingTransport();
        using var httpClient = new HttpClient(disposalTrackingTransport);
        var galleryRuntimeOptions = new GalleryRuntimeOptions { StateDirectory = testVault.Root + "-state" };
        using var galleryService = GalleryService.Create(testVault.Root, galleryRuntimeOptions, httpClient);
        const bool ExpectedIsDisposed = true;

        // Act
        galleryService.Dispose();

        // Assert
        Assert.Equal(ExpectedIsDisposed, disposalTrackingTransport.IsDisposed);
    }

    [Fact]
    public void DomainConstruction_WhenHistoryCannotBeRead_ThenOwnedTransportIsDisposed()
    {
        // Arrange
        using var testVault = new TestVault();
        using var disposalTrackingTransport = new DisposalTrackingTransport();
        using var httpClient = new HttpClient(disposalTrackingTransport);
        var stateDirectory = testVault.Root + "-state";
        Directory.CreateDirectory(stateDirectory);
        using var lockedHistory = new FileStream(Path.Combine(stateDirectory, "last-vault.json"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var galleryRuntimeOptions = new GalleryRuntimeOptions { StateDirectory = stateDirectory };
        const bool ExpectedIsDisposed = true;

        // Act
        Assert.Throws<IOException>(() => GalleryService.Create(testVault.Root, galleryRuntimeOptions, httpClient));

        // Assert
        Assert.Equal(ExpectedIsDisposed, disposalTrackingTransport.IsDisposed);
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
