using Gallery.Web.Exceptions;
using Gallery.Web.Services.Processors;
using Gallery.Web.Services.DataManagers;
using Gallery.Web.Services.Sessions;
using System.Security.Cryptography;
using Gallery.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gallery.Tests;

// Direct crypto/session tests pin primitive guarantees that service-level vault flows cannot isolate.
[Trait("Category", "class_test")]
public sealed class CryptoTests
{
    [Fact]
    public void Encryption_WhenRepeatedWithSameInput_UsesUniqueNoncesAndRoundTrips()
    {
        // Arrange
        var key = RandomNumberGenerator.GetBytes(32);

        // Act
        var a = VaultCrypto.Encrypt("private image"u8, key, "image:123");
        var b = VaultCrypto.Encrypt("private image"u8, key, "image:123");
        var decrypted = VaultCrypto.Decrypt(a, key, "image:123");

        // Assert
        Assert.NotEqual(a, b);
        Assert.Equal("private image"u8.ToArray(), decrypted);
        Assert.Equal("DPNG0001"u8.ToArray(), a[..8]);
    }

    [Theory]
    // Offsets cover the format marker, nonce, authentication tag, and ciphertext segments.
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(36)]
    public void Ciphertext_WhenTamperedAtAnySegment_ThrowsCryptographicException(int offset)
    {
        // Arrange
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = VaultCrypto.Encrypt("private image"u8, key, "image:123");

        // Act
        encrypted[offset] ^= 1;
        var error = offset == 0
            ? Assert.Throws<CryptographicException>(() => VaultCrypto.Decrypt(encrypted, key, "image:123"))
            : Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Decrypt(encrypted, key, "image:123"));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(error);
    }

    [Fact]
    public void Ciphertext_WhenKeyOrContextIsWrong_ThrowsCryptographicException()
    {
        // Arrange
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = VaultCrypto.Encrypt("private image"u8, key, "image:123");

        // Act
        var wrongKey = Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Decrypt(encrypted, RandomNumberGenerator.GetBytes(32), "image:123"));
        var wrongContext = Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Decrypt(encrypted, key, "thumbnail:123"));
        var truncated = Assert.Throws<CryptographicException>(() => VaultCrypto.Decrypt(encrypted[..20], key, "image:123"));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(wrongKey);
        Assert.IsAssignableFrom<CryptographicException>(wrongContext);
        Assert.IsAssignableFrom<CryptographicException>(truncated);
    }

    [Fact]
    public void RecoveryPhrase_WhenCreatedAndNormalized_Has32WordsAndIgnoresWhitespace()
    {
        // Arrange
        var phrase = VaultCrypto.CreateRecoveryPhrase();
        var whitespacePhrase = " \n" + phrase.ToUpperInvariant().Replace(" ", " \n ") + "\t";
        var unknownWords = string.Join(' ', Enumerable.Repeat("no-suchword", 32));

        // Act
        var normalized = VaultCrypto.NormalizeRecovery(whitespacePhrase);
        var shortPhraseError = Assert.Throws<InvalidOperationException>(() => VaultCrypto.NormalizeRecovery("wrong words"));
        var unknownWordError = Assert.Throws<InvalidOperationException>(() => VaultCrypto.NormalizeRecovery(unknownWords));

        // Assert
        Assert.Equal(32, phrase.Split(' ').Length);
        Assert.Equal(phrase, normalized);
        Assert.IsType<InvalidOperationException>(shortPhraseError);
        Assert.IsType<InvalidOperationException>(unknownWordError);
    }

    [Fact]
    public async Task Vault_WhenPassphraseAndRecoveryRotateAcrossRestart_UnlocksOnlyWithCurrentCredentials()
    {
        // Arrange
        using var fixture = new TestVault();
        var (key, recovery) = await fixture.Vault.CreateAsync(TestVault.Passphrase);
        var restarted = new VaultStore(fixture.Vault.Root);

        // Act
        var passphraseKey = await restarted.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var recoveryKey = await restarted.UnlockWithRecoveryPhraseAsync(recovery);
        var wrongPassphrase = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => restarted.UnlockWithPassphraseAsync("wrong passphrase"));
        var wrongRotationKey = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => restarted.ChangePassphraseAsync(RandomNumberGenerator.GetBytes(32), "new strong secret passphrase"));
        await restarted.ChangePassphraseAsync(key, "new strong secret passphrase");
        var previousPassphrase = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => restarted.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var rotatedKey = await restarted.UnlockWithPassphraseAsync("new strong secret passphrase");
        var rotatedRecoveryKey = await restarted.UnlockWithRecoveryPhraseAsync(recovery);
        var header = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "vault.json"));
        var repeatedCreation = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.CreateAsync(TestVault.Passphrase));

        // Assert
        Assert.Equal(key, passphraseKey);
        Assert.Equal(key, recoveryKey);
        Assert.IsAssignableFrom<CryptographicException>(wrongPassphrase);
        Assert.IsAssignableFrom<CryptographicException>(wrongRotationKey);
        Assert.IsAssignableFrom<CryptographicException>(previousPassphrase);
        Assert.Equal(key, rotatedKey);
        Assert.Equal(key, rotatedRecoveryKey);
        Assert.DoesNotContain(TestVault.Passphrase, header);
        Assert.DoesNotContain(recovery, header);
        Assert.DoesNotContain(Convert.ToBase64String(key), header);
        Assert.IsType<InvalidOperationException>(repeatedCreation);
    }

    [Fact]
    public async Task Vault_WhenPassphraseIsTooShort_RejectsCreationWithoutCreatingVault()
    {
        // Arrange
        using var fixture = new TestVault();

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.CreateAsync("short"));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.False(fixture.Vault.Exists);
    }

    [Fact]
    public void Session_WhenLocked_PreservesCallerKeyAndClearsDisposedLease()
    {
        // Arrange
        using var session = new VaultSession();
        var key = RandomNumberGenerator.GetBytes(32);
        var originalKey = key.ToArray();
        session.Unlock(key);
        var lease = session.AcquireUnlockedSession().Borrow();
        var borrowedKey = lease.Bytes;

        // Act
        session.Lock();
        lease.Dispose();
        var lockedBorrowError = Assert.Throws<VaultLockedException>(() => session.AcquireUnlockedSession().Borrow());

        // Assert
        Assert.False(session.IsUnlocked);
        Assert.Equal(originalKey, key);
        Assert.All(borrowedKey, value => Assert.Equal(0, value));
        Assert.IsType<VaultLockedException>(lockedBorrowError);
    }

    [Fact]
    public async Task Session_WhenBorrowedDuringLockAndUnlock_ReturnsWholeIndependentSnapshots()
    {
        // Arrange
        using var session = new VaultSession();
        session.Unlock(Enumerable.Repeat((byte)1, 32).ToArray());
        var inconsistentSnapshots = 0;
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                session.Unlock(Enumerable.Repeat((byte)(index % 2 + 1), 32).ToArray());
                if (index % 3 == 0) session.Lock();
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                try
                {
                    using var lease = session.AcquireUnlockedSession().Borrow();
                    if (lease.Bytes.Any(value => value != lease.Bytes[0]))
                        Interlocked.Increment(ref inconsistentSnapshots);
                }
                catch (VaultLockedException)
                {
                    // A concurrent lock may win before the read; a partial key snapshot is never valid.
                }
            }
        }));

        // Act
        await Task.WhenAll(readers.Append(writer));

        // Assert
        Assert.Equal(0, inconsistentSnapshots);
    }
}
