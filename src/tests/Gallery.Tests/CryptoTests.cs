using System.Security.Cryptography;
using Gallery.Services;
using Microsoft.Extensions.Configuration;

namespace Gallery.Tests;

public sealed class CryptoTests
{
    [Fact]
    public void EncryptionRoundTripsAndUsesUniqueNonces()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var a = VaultCrypto.Encrypt("private image"u8, key, "image:123");
        var b = VaultCrypto.Encrypt("private image"u8, key, "image:123");
        Assert.NotEqual(a, b);
        Assert.Equal("private image"u8.ToArray(), VaultCrypto.Decrypt(a, key, "image:123"));
        Assert.Equal("DPNG0001"u8.ToArray(), a[..8]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(36)]
    public void TamperingIsRejected(int offset)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = VaultCrypto.Encrypt("private image"u8, key, "image:123");
        encrypted[offset] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(encrypted, key, "image:123"));
    }

    [Fact]
    public void WrongKeyAndWrongContextAreRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = VaultCrypto.Encrypt("private image"u8, key, "image:123");
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(encrypted, RandomNumberGenerator.GetBytes(32), "image:123"));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(encrypted, key, "thumbnail:123"));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Decrypt(encrypted[..20], key, "image:123"));
    }

    [Fact]
    public void RecoveryHas32WordsAndNormalizesWhitespace()
    {
        var phrase = VaultCrypto.CreateRecoveryPhrase();
        Assert.Equal(32, phrase.Split(' ').Length);
        Assert.Equal(phrase, VaultCrypto.NormalizeRecovery(" \n" + phrase.ToUpperInvariant().Replace(" ", " \n ") + "\t"));
        Assert.Throws<InvalidOperationException>(() => VaultCrypto.NormalizeRecovery("wrong words"));
        Assert.Throws<InvalidOperationException>(() => VaultCrypto.NormalizeRecovery(string.Join(' ', Enumerable.Repeat("no-suchword", 32))));
    }

    [Fact]
    public async Task PassphraseRecoveryRotationAndRestartWork()
    {
        using var fixture = new TestVault();
        var (key, recovery) = await fixture.Vault.CreateAsync(TestVault.Passphrase);
        var restarted = new VaultStore(fixture.Configuration);
        Assert.Equal(key, await restarted.UnlockAsync(TestVault.Passphrase, false));
        Assert.Equal(key, await restarted.UnlockAsync(recovery, true));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => restarted.UnlockAsync("wrong passphrase", false));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => restarted.ChangePassphraseAsync(RandomNumberGenerator.GetBytes(32), "new strong secret passphrase"));
        await restarted.ChangePassphraseAsync(key, "new strong secret passphrase");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => restarted.UnlockAsync(TestVault.Passphrase, false));
        Assert.Equal(key, await restarted.UnlockAsync("new strong secret passphrase", false));
        Assert.Equal(key, await restarted.UnlockAsync(recovery, true));
        var header = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "vault.json"));
        Assert.DoesNotContain(TestVault.Passphrase, header);
        Assert.DoesNotContain(recovery, header);
        Assert.DoesNotContain(Convert.ToBase64String(key), header);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.CreateAsync(TestVault.Passphrase));
    }

    [Fact]
    public async Task ShortPassphraseCannotCreateVault()
    {
        using var fixture = new TestVault();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.CreateAsync("short"));
        Assert.False(fixture.Vault.Exists);
    }

    [Fact]
    public void LockZeroesTheSessionKey()
    {
        using var session = new VaultSession();
        var key = RandomNumberGenerator.GetBytes(32);
        session.Unlock(key);
        session.Lock();
        Assert.False(session.IsUnlocked);
        Assert.All(key, value => Assert.Equal(0, value));
        Assert.Throws<InvalidOperationException>(() => session.Borrow());
    }
}

internal sealed class TestVault : IDisposable
{
    public const string Passphrase = "a strong unique test passphrase";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gallery-tests-" + Guid.NewGuid().ToString("N"));
    public IConfiguration Configuration { get; }
    public VaultStore Vault { get; }
    public GalleryStore Store { get; }
    public VaultSession Session { get; } = new();
    public GalleryService Service { get; }

    public TestVault()
    {
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:DataDirectory"] = Root
        }).Build();
        Vault = new(Configuration);
        Store = new(Vault);
        Service = new(Store, Vault, Session);
    }

    public async Task InitializeAsync()
    {
        var created = await Vault.CreateAsync(Passphrase);
        Session.Unlock(created.Key);
    }

    public void Dispose()
    {
        Session.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
