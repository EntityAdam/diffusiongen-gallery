using System.Security.Cryptography;
using Gallery.Services;
using Microsoft.Extensions.Configuration;

namespace Gallery.Tests;

// Direct crypto/session tests pin primitive guarantees that service-level vault flows cannot isolate.
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
        var error = Record.Exception(() => VaultCrypto.Decrypt(encrypted, key, "image:123"));

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
        var wrongKey = Record.Exception(() => VaultCrypto.Decrypt(encrypted, RandomNumberGenerator.GetBytes(32), "image:123"));
        var wrongContext = Record.Exception(() => VaultCrypto.Decrypt(encrypted, key, "thumbnail:123"));
        var truncated = Record.Exception(() => VaultCrypto.Decrypt(encrypted[..20], key, "image:123"));

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
        var shortPhraseError = Record.Exception(() => VaultCrypto.NormalizeRecovery("wrong words"));
        var unknownWordError = Record.Exception(() => VaultCrypto.NormalizeRecovery(unknownWords));

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
        var wrongPassphrase = await Record.ExceptionAsync(() => restarted.UnlockWithPassphraseAsync("wrong passphrase"));
        var wrongRotationKey = await Record.ExceptionAsync(() => restarted.ChangePassphraseAsync(RandomNumberGenerator.GetBytes(32), "new strong secret passphrase"));
        await restarted.ChangePassphraseAsync(key, "new strong secret passphrase");
        var previousPassphrase = await Record.ExceptionAsync(() => restarted.UnlockWithPassphraseAsync(TestVault.Passphrase));
        var rotatedKey = await restarted.UnlockWithPassphraseAsync("new strong secret passphrase");
        var rotatedRecoveryKey = await restarted.UnlockWithRecoveryPhraseAsync(recovery);
        var header = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "vault.json"));
        var repeatedCreation = await Record.ExceptionAsync(() => fixture.Vault.CreateAsync(TestVault.Passphrase));

        // Assert
        Assert.Equal(key, passphraseKey);
        Assert.Equal(key, recoveryKey);
        Assert.IsAssignableFrom<CryptographicException>(wrongPassphrase);
        Assert.IsAssignableFrom<CryptographicException>(wrongRotationKey);
        Assert.IsAssignableFrom<CryptographicException>(previousPassphrase);
        Assert.Equal(key, rotatedKey);
        Assert.Equal(key, rotatedRecoveryKey);
        TestAssertions.DoesNotContain(TestVault.Passphrase, header, "the passphrase is absent from the vault header");
        TestAssertions.DoesNotContain(recovery, header, "the recovery phrase is absent from the vault header");
        TestAssertions.DoesNotContain(Convert.ToBase64String(key), header, "the session key is absent from the vault header");
        Assert.IsType<InvalidOperationException>(repeatedCreation);
    }

    [Fact]
    public async Task Vault_WhenPassphraseIsTooShort_RejectsCreationWithoutCreatingVault()
    {
        // Arrange
        using var fixture = new TestVault();

        // Act
        var error = await Record.ExceptionAsync(() => fixture.Vault.CreateAsync("short"));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        TestAssertions.False(fixture.Vault.Exists, "the vault does not exist after rejecting a short passphrase");
    }

    [Fact]
    public void Session_WhenLocked_PreservesCallerKeyAndClearsDisposedLease()
    {
        // Arrange
        using var session = new VaultSession();
        var key = RandomNumberGenerator.GetBytes(32);
        var originalKey = key.ToArray();
        session.Unlock(key);
        var lease = session.Borrow();
        var borrowedKey = lease.Bytes;

        // Act
        session.Lock();
        lease.Dispose();
        var lockedBorrowError = Record.Exception(() => session.Borrow());

        // Assert
        TestAssertions.False(session.IsUnlocked, "the session is locked");
        Assert.Equal(originalKey, key);
        Assert.All(borrowedKey, value => Assert.Equal(0, value));
        Assert.IsType<InvalidOperationException>(lockedBorrowError);
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
                    using var lease = session.Borrow();
                    if (lease.Bytes.Any(value => value != lease.Bytes[0]))
                        Interlocked.Increment(ref inconsistentSnapshots);
                }
                catch (InvalidOperationException)
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

internal static class TestAssertions
{
    public static void True(bool actual, string expected) =>
        Assert.True(actual, $"Expected: {expected}; Actual: {actual}.");

    public static void False(bool actual, string expected) =>
        Assert.False(actual, $"Expected: {expected}; Actual: {actual}.");

    public static void Contains(string expected, string actual, string behavior) =>
        Assert.True(actual.Contains(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (containing \"{expected}\"); Actual: \"{actual}\".");

    public static void StartsWith(string expected, string actual, string behavior) =>
        Assert.True(actual.StartsWith(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (starting with \"{expected}\"); Actual: \"{actual}\".");

    public static void EndsWith(string expected, string actual, string behavior) =>
        Assert.True(actual.EndsWith(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (ending with \"{expected}\"); Actual: \"{actual}\".");

    public static void DoesNotContain(string expected, string actual, string behavior) =>
        Assert.False(actual.Contains(expected, StringComparison.Ordinal),
            $"Expected: {behavior} (not containing \"{expected}\"); Actual: \"{actual}\".");

    public static void DoesNotContain<T>(T expected, IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.False(values.Contains(expected),
            $"Expected: {behavior} (excluding \"{expected}\"); Actual: [{string.Join(", ", values)}].");
    }

    public static void Contains<T>(T expected, IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Contains(expected),
            $"Expected: {behavior} (including \"{expected}\"); Actual: [{string.Join(", ", values)}].");
    }

    public static void Contains<T>(IEnumerable<T> actual, Predicate<T> predicate, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Any(value => predicate(value)),
            $"Expected: {behavior}; Actual: [{string.Join(", ", values)}].");
    }

    public static T Single<T>(IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Length == 1,
            $"Expected: exactly one value because {behavior}; Actual: {values.Length} values [{string.Join(", ", values)}].");
        return values[0];
    }

    public static void Null(object? actual, string behavior) =>
        Assert.True(actual is null, $"Expected: null because {behavior}; Actual: {actual ?? "<null>"}.");

    public static T NotNull<T>(T? actual, string behavior) where T : class
    {
        Assert.True(actual is not null, $"Expected: a non-null value because {behavior}; Actual: {actual?.ToString() ?? "<null>"}.");
        return actual!;
    }

    public static void Empty<T>(IEnumerable<T> actual, string behavior)
    {
        var values = actual.ToArray();
        Assert.True(values.Length == 0,
            $"Expected: no values because {behavior}; Actual: {values.Length} values [{string.Join(", ", values)}].");
    }

    public static T Throws<T>(Action action, string behavior, params string[] expectedMessageParts) where T : Exception
    {
        var exception = Assert.Throws<T>(action);
        AssertMessageParts(exception, behavior, expectedMessageParts);
        return exception;
    }

    public static T ThrowsAny<T>(Action action, string behavior, params string[] expectedMessageParts) where T : Exception
    {
        var exception = Assert.ThrowsAny<T>(action);
        AssertMessageParts(exception, behavior, expectedMessageParts);
        return exception;
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action, string behavior, params string[] expectedMessageParts)
        where T : Exception
    {
        var exception = await Assert.ThrowsAsync<T>(action);
        AssertMessageParts(exception, behavior, expectedMessageParts);
        return exception;
    }

    public static async Task<T> ThrowsAnyAsync<T>(Func<Task> action, string behavior, params string[] expectedMessageParts)
        where T : Exception
    {
        var exception = await Assert.ThrowsAnyAsync<T>(action);
        AssertMessageParts(exception, behavior, expectedMessageParts);
        return exception;
    }

    private static void AssertMessageParts(Exception exception, string behavior, IEnumerable<string> expectedMessageParts)
    {
        foreach (var expectedPart in expectedMessageParts)
            Contains(expectedPart, exception.Message, behavior);
    }

    public static T Exception<T>(Exception? actual, string behavior, params string[] expectedMessageParts) where T : Exception
    {
        var exception = Assert.IsType<T>(actual);
        AssertMessageParts(exception, behavior, expectedMessageParts);
        return exception;
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

    public TestVault(VideoTools? video = null)
    {
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:DataDirectory"] = Root
        }).Build();
        Vault = new(Root);
        Store = new(Path.Combine(Root, "gallery.db"));
        Service = new(Store, Vault, Session, video);
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
