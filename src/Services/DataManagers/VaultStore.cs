using System.Collections.Immutable;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Services;

internal sealed record KeySlot(ImmutableArray<byte> Salt, ImmutableArray<byte> WrappedKey);
internal sealed record VaultHeader(
    int Version,
    KeySlot Passphrase,
    KeySlot Recovery,
    ImmutableArray<byte> KeyCheck);

/// <summary>Persists and unlocks the encrypted vault header and root key.</summary>
internal sealed class VaultStore
{
    private const int VaultKeySizeBytes = 32;
    private const int SaltSizeBytes = 32;
    private const int VaultHeaderVersion = 1;
    private const string PassphrasePurpose = "passphrase";
    private const string RecoveryPurpose = "recovery";
    private const string VaultCheckPurpose = "vault-check";
    private const string VaultCheckPlaintext = "gallery-vault";
    private static readonly JsonSerializerOptions vaultJsonOptions = CreateVaultJsonOptions();

    private readonly SemaphoreSlim gate = new(1);
    public string Root { get; }
    private string HeaderPath => Path.Combine(Root, "vault.json");
    public bool Exists => File.Exists(HeaderPath);

    public VaultStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 14)
            throw new InvalidOperationException("Use a passphrase of at least 14 characters; a long unique phrase is recommended.");
    }

    public async Task<(byte[] Key, string Recovery)> CreateAsync(string passphrase)
    {
        ValidatePassphrase(passphrase);
        await gate.WaitAsync();
        var key = RandomNumberGenerator.GetBytes(VaultKeySizeBytes);
        try
        {
            if (Exists) throw new InvalidOperationException("A vault already exists. Unlock it instead.");
            Directory.CreateDirectory(Root);
            var (vaultHeader, recoveryPhrase) = CreateInitialVaultHeader(key, passphrase);
            await CreateHeaderAsync(vaultHeader);
            return (key, recoveryPhrase);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally { gate.Release(); }
    }

    public Task<byte[]> UnlockWithPassphraseAsync(string passphrase) =>
        UnlockCredentialAsync(
            passphrase,
            static vaultHeader => vaultHeader.Passphrase,
            static secret => secret,
            PassphrasePurpose);

    public Task<byte[]> UnlockWithRecoveryPhraseAsync(string recoveryPhrase) =>
        UnlockCredentialAsync(
            recoveryPhrase,
            static vaultHeader => vaultHeader.Recovery,
            VaultCrypto.NormalizeRecovery,
            RecoveryPurpose);

    public async Task ChangePassphraseAsync(byte[] key, string passphrase)
    {
        ValidatePassphrase(passphrase);
        await gate.WaitAsync();
        try
        {
            var vaultHeader = await ReadHeaderAsync();
            var keyCheckPlaintext = VaultCrypto.Decrypt(vaultHeader.KeyCheck.AsSpan(), key, VaultCheckPurpose);
            CryptographicOperations.ZeroMemory(keyCheckPlaintext);
            var passphraseKeySlot = Wrap(key, passphrase, PassphrasePurpose);
            await ReplaceHeaderAsync(vaultHeader with { Passphrase = passphraseKeySlot });
        }
        finally { gate.Release(); }
    }

    private static (VaultHeader VaultHeader, string RecoveryPhrase) CreateInitialVaultHeader(
        byte[] key,
        string passphrase)
    {
        var recoveryPhrase = VaultCrypto.CreateRecoveryPhrase();
        var passphraseKeySlot = Wrap(key, passphrase, PassphrasePurpose);
        var recoveryKeySlot = Wrap(key, recoveryPhrase, RecoveryPurpose);
        var keyCheck = VaultCrypto.Encrypt(
            Encoding.UTF8.GetBytes(VaultCheckPlaintext),
            key,
            VaultCheckPurpose);
        var vaultHeader = new VaultHeader(
            VaultHeaderVersion,
            passphraseKeySlot,
            recoveryKeySlot,
            ImmutableArray.CreateRange(keyCheck));
        return (vaultHeader, recoveryPhrase);
    }

    private async Task<byte[]> UnlockCredentialAsync(
        string secret,
        Func<VaultHeader, KeySlot> keySlotSelector,
        Func<string, string> normalizeSecret,
        string purpose)
    {
        await gate.WaitAsync();
        try
        {
            var vaultHeader = await ReadHeaderAsync();
            var keySlot = keySlotSelector(vaultHeader);
            var normalizedSecret = normalizeSecret(secret);
            return UnwrapVaultKey(keySlot, normalizedSecret, purpose);
        }
        finally { gate.Release(); }
    }

    private static byte[] UnwrapVaultKey(KeySlot keySlot, string secret, string purpose)
    {
        var derivedKey = VaultCrypto.Derive(secret, keySlot.Salt.AsSpan());
        try
        {
            var vaultKey = VaultCrypto.Decrypt(keySlot.WrappedKey.AsSpan(), derivedKey, purpose);
            if (vaultKey.Length != VaultKeySizeBytes)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
                throw new CryptographicException("Invalid vault key.");
            }
            return vaultKey;
        }
        finally { CryptographicOperations.ZeroMemory(derivedKey); }
    }

    private async Task<VaultHeader> ReadHeaderAsync()
    {
        var vaultHeader = JsonSerializer.Deserialize<VaultHeader>(
            await File.ReadAllTextAsync(HeaderPath),
            vaultJsonOptions);
        if (vaultHeader is null)
            throw new InvalidOperationException("Vault header is empty.");
        if (!IsValidVaultHeader(vaultHeader))
            throw new InvalidOperationException("Unsupported or corrupt vault header.");
        return vaultHeader;
    }

    private static bool IsValidVaultHeader(VaultHeader vaultHeader) =>
        vaultHeader.Version == VaultHeaderVersion &&
        IsValidKeySlot(vaultHeader.Passphrase) &&
        IsValidKeySlot(vaultHeader.Recovery) &&
        !vaultHeader.KeyCheck.IsDefaultOrEmpty;

    private static bool IsValidKeySlot(KeySlot? keySlot) =>
        keySlot is not null &&
        !keySlot.Salt.IsDefault &&
        keySlot.Salt.Length == SaltSizeBytes &&
        !keySlot.WrappedKey.IsDefaultOrEmpty;

    private static KeySlot Wrap(byte[] key, string secret, string purpose)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var derivedKey = VaultCrypto.Derive(secret, salt);
        try
        {
            var wrappedKey = VaultCrypto.Encrypt(key, derivedKey, purpose);
            return new KeySlot(
                ImmutableArray.CreateRange(salt),
                ImmutableArray.CreateRange(wrappedKey));
        }
        finally { CryptographicOperations.ZeroMemory(derivedKey); }
    }

    private Task CreateHeaderAsync(VaultHeader vaultHeader) =>
        WriteHeaderAsync(vaultHeader, MoveNewHeaderIntoPlace);

    private Task ReplaceHeaderAsync(VaultHeader vaultHeader) =>
        WriteHeaderAsync(vaultHeader, ReplaceExistingHeader);

    private async Task WriteHeaderAsync(
        VaultHeader vaultHeader,
        Action<string, string> finalizeHeaderWrite)
    {
        var temporaryHeaderPath = HeaderPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryHeaderPath,
                JsonSerializer.Serialize(vaultHeader, vaultJsonOptions));
            finalizeHeaderWrite(temporaryHeaderPath, HeaderPath);
        }
        finally
        {
            if (File.Exists(temporaryHeaderPath))
                File.Delete(temporaryHeaderPath);
        }
    }

    private static void MoveNewHeaderIntoPlace(string temporaryHeaderPath, string headerPath) =>
        File.Move(temporaryHeaderPath, headerPath);

    private static void ReplaceExistingHeader(string temporaryHeaderPath, string headerPath) =>
        File.Move(temporaryHeaderPath, headerPath, overwrite: true);

    private static JsonSerializerOptions CreateVaultJsonOptions()
    {
        var serializerOptions = new JsonSerializerOptions();
        serializerOptions.Converters.Add(new ImmutableByteArrayJsonConverter());
        return serializerOptions;
    }

    private sealed class ImmutableByteArrayJsonConverter : JsonConverter<ImmutableArray<byte>>
    {
        public override ImmutableArray<byte> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Vault byte data must be base64-encoded.");
            return ImmutableArray.CreateRange(reader.GetBytesFromBase64());
        }

        public override void Write(
            Utf8JsonWriter writer,
            ImmutableArray<byte> immutableBytes,
            JsonSerializerOptions options)
        {
            if (immutableBytes.IsDefault)
                throw new JsonException("Vault byte data cannot be uninitialized.");
            writer.WriteBase64StringValue(immutableBytes.AsSpan());
        }
    }
}
