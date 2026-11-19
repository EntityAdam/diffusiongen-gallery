// Class tests pin DI scope ownership, startup snapshots and transport policy, which HTTP cannot observe.
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Web.Composition;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Gallery.Core.Features.Settings;

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
        var vault = serviceScope.ServiceProvider.GetRequiredService<IGalleryVault>();
        var expectedScenario = ExpectedScenario.CreateFrom("Scopes use the state directory captured at registration, not later configuration changes.",
            new { VaultRoot = testVault.Root, ChangedDirectoryExists = false });

        // Act
        await vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);

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
    public void DomainRegistration_WhenInspected_ThenExposesOnlyScopedCapabilityContractsAndTheScopeOwner()
    {
        // Arrange
        using var testVault = new TestVault();
        var serviceCollection = new ServiceCollection();
        Type[] expectedDomainTypes =
        [
            typeof(GalleryScope), typeof(IGalleryBackup), typeof(IGalleryCatalog), typeof(IGalleryCollections),
            typeof(IGallerySettings), typeof(IGalleryDriveOperations), typeof(IGalleryDuplicates), typeof(IGalleryRecordEditor),
            typeof(IGalleryIngestion), typeof(IGalleryVault), typeof(IGalleryMedia), typeof(IGalleryMediaMaintenance),
            typeof(IGalleryRestore), typeof(IGalleryStorage), typeof(IGalleryTags), typeof(IGalleryVision)
        ];

        // Act
        GalleryServiceRegistration.Register(serviceCollection, testVault.Configuration, testVault.Root);

        // Assert - gather registrations without changing them
        var actualDomainTypes = serviceCollection
            .Where(serviceDescriptor => serviceDescriptor.ServiceType.Assembly == typeof(GalleryScope).Assembly)
            .ToArray();

        // Assert
        Assert.All(actualDomainTypes, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
        Assert.Equal(expectedDomainTypes, actualDomainTypes.Select(descriptor => descriptor.ServiceType));

        using var provider = serviceCollection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        foreach (var capability in expectedDomainTypes.Where(type => type.IsInterface))
        {
            var resolved = first.ServiceProvider.GetRequiredService(capability);
            Assert.Same(resolved, first.ServiceProvider.GetRequiredService(capability));
            Assert.NotSame(resolved, second.ServiceProvider.GetRequiredService(capability));
        }
    }

    [Fact]
    public async Task DomainCapabilities_WhenUsedTogether_ThenShareTheCurrentVaultSessionAndCatalog()
    {
        using var testVault = new TestVault();
        var services = new ServiceCollection();
        GalleryServiceRegistration.Register(services, testVault.Configuration, testVault.Root);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IGalleryVault>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IGalleryIngestion>();
        var catalog = scope.ServiceProvider.GetRequiredService<IGalleryCatalog>();
        var editor = scope.ServiceProvider.GetRequiredService<IGalleryRecordEditor>();

        await vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        using var input = new MemoryStream(GalleryTests.MakePng());
        var imported = await ingestion.ImportAsync(input, "shared.png", "source");
        await editor.UpdateAsync(imported.Id, edit => editor.SetCuration(edit, 4, null));

        var stored = Assert.Single(await catalog.ListAsync());
        Assert.Equal(imported.Id, stored.Id);
        Assert.Equal(4, stored.Rating);

        vault.LockVault();
        await Assert.ThrowsAsync<VaultLockedException>(() => catalog.ListAsync());
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
        var galleryServiceOwner = serviceScopeOwner.ServiceProvider.GetRequiredService<IGalleryVault>();
        var galleryServiceWaiting = serviceScopeWaiting.ServiceProvider.GetRequiredService<IGalleryVault>();
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
        var galleryServiceClosed = serviceScopeClosed.ServiceProvider.GetRequiredService<IGalleryVault>();
        var galleryServiceNext = serviceScopeNext.ServiceProvider.GetRequiredService<IGalleryVault>();
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
            () => testVault.Scope.Settings.SaveViewAsync(invalidName, new()));

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
    public void GalleryScope_WhenDisposed_ThenDisposesOwnedTransport()
    {
        // Arrange
        using var testVault = new TestVault();
        using var disposalTrackingTransport = new DisposalTrackingTransport();
        using var httpClient = new HttpClient(disposalTrackingTransport);
        var galleryRuntimeOptions = new GalleryRuntimeOptions { StateDirectory = testVault.Root + "-state" };
        using var galleryScope = GalleryScope.Create(testVault.Root, galleryRuntimeOptions, httpClient);
        const bool ExpectedIsDisposed = true;

        // Act
        galleryScope.Dispose();

        // Assert
        Assert.Equal(ExpectedIsDisposed, disposalTrackingTransport.IsDisposed);
    }

    [Fact]
    public void GalleryScope_WhenHistoryCannotBeRead_ThenDisposesOwnedTransport()
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
        Assert.Throws<IOException>(() => GalleryScope.Create(testVault.Root, galleryRuntimeOptions, httpClient));

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
