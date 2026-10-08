using System.Security.Cryptography;
using System.Text.Json;

namespace Gallery.Services;

public sealed record KeySlot(byte[] Salt, byte[] WrappedKey);
public sealed record VaultHeader(int Version, KeySlot Passphrase, KeySlot Recovery, byte[] KeyCheck);

public sealed class VaultStore
{
    private readonly SemaphoreSlim gate = new(1);
    public string Root { get; }
    private string HeaderPath => Path.Combine(Root, "vault.json");
    public bool Exists => File.Exists(HeaderPath);

    public VaultStore(IConfiguration configuration)
    {
        Root = Path.GetFullPath(configuration["Gallery:DataDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiffusionGenGallery02"));
    }

    public static void ValidatePassphrase(string passphrase)
    {
        if (passphrase.Length < 14 || string.IsNullOrWhiteSpace(passphrase))
            throw new InvalidOperationException("Use a passphrase of at least 14 characters; a long unique phrase is recommended.");
    }

    public async Task<(byte[] Key, string Recovery)> CreateAsync(string passphrase)
    {
        ValidatePassphrase(passphrase);
        await gate.WaitAsync();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (Exists) throw new InvalidOperationException("A vault already exists. Unlock it instead.");
            Directory.CreateDirectory(Root);
            var recovery = VaultCrypto.CreateRecoveryPhrase();
            var header = new VaultHeader(1, Wrap(key, passphrase, "passphrase"), Wrap(key, recovery, "recovery"),
                VaultCrypto.Encrypt("gallery-vault"u8, key, "vault-check"));
            await WriteHeaderAsync(header, create: true);
            return (key, recovery);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> UnlockAsync(string secret, bool recovery)
    {
        await gate.WaitAsync();
        try
        {
            var header = await ReadHeaderAsync();
            var slot = recovery ? header.Recovery : header.Passphrase;
            var derived = VaultCrypto.Derive(recovery ? VaultCrypto.NormalizeRecovery(secret) : secret, slot.Salt);
            try
            {
                var key = VaultCrypto.Decrypt(slot.WrappedKey, derived, recovery ? "recovery" : "passphrase");
                if (key.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException("Invalid vault key.");
                }
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(derived); }
        }
        finally { gate.Release(); }
    }

    public async Task ChangePassphraseAsync(byte[] key, string passphrase)
    {
        ValidatePassphrase(passphrase);
        await gate.WaitAsync();
        try
        {
            var header = await ReadHeaderAsync();
            // Authenticate the supplied master key before replacing a credential slot.
            var check = VaultCrypto.Decrypt(header.KeyCheck, key, "vault-check");
            CryptographicOperations.ZeroMemory(check);
            await WriteHeaderAsync(header with { Passphrase = Wrap(key, passphrase, "passphrase") }, create: false);
        }
        finally { gate.Release(); }
    }

    private async Task<VaultHeader> ReadHeaderAsync()
    {
        var header = JsonSerializer.Deserialize<VaultHeader>(await File.ReadAllTextAsync(HeaderPath))
            ?? throw new InvalidOperationException("Vault header is empty.");
        if (header.Version != 1 || header.Passphrase.Salt.Length != 32 || header.Recovery.Salt.Length != 32)
            throw new InvalidOperationException("Unsupported or corrupt vault header.");
        return header;
    }

    private static KeySlot Wrap(byte[] key, string secret, string purpose)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var derived = VaultCrypto.Derive(secret, salt);
        try { return new(salt, VaultCrypto.Encrypt(key, derived, purpose)); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private async Task WriteHeaderAsync(VaultHeader header, bool create)
    {
        var temp = HeaderPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(header));
            File.Move(temp, HeaderPath, overwrite: !create);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
