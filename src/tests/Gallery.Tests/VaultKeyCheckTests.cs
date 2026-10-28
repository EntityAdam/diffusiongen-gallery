using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gallery.Core.Services.DataManagers;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class VaultKeyCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlock_WhenValidSlotBelongsToAnotherVault_RejectsKeyAndPreservesOriginalCredentials(bool recovery)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".vault-key-check-" + Guid.NewGuid().ToString("N"));
        var first = new VaultStore(Path.Combine(root, "first"));
        var second = new VaultStore(Path.Combine(root, "second"));
        byte[]? firstKey = null;
        byte[]? secondKey = null;
        try
        {
            var firstCreated = await first.CreateAsync(TestVault.Passphrase);
            firstKey = firstCreated.Key;
            var secondCreated = await second.CreateAsync(TestVault.Passphrase);
            secondKey = secondCreated.Key;
            var headerPath = Path.Combine(first.Root, "vault.json");
            var originalHeader = await File.ReadAllTextAsync(headerPath);
            var firstHeader = JsonNode.Parse(originalHeader)!;
            var secondHeader = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(second.Root, "vault.json")))!;
            var slot = recovery ? "Recovery" : "Passphrase";
            firstHeader[slot] = secondHeader[slot]!.DeepClone();
            await File.WriteAllTextAsync(headerPath, firstHeader.ToJsonString());

            var foreignCredential = recovery ? secondCreated.Recovery : TestVault.Passphrase;
            var foreignKey = recovery
                ? await second.UnlockWithRecoveryPhraseAsync(foreignCredential)
                : await second.UnlockWithPassphraseAsync(foreignCredential);
            try
            {
                Assert.Equal(secondKey, foreignKey);
                Assert.NotEqual(firstKey, foreignKey);
                await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
                    recovery
                        ? first.UnlockWithRecoveryPhraseAsync(foreignCredential)
                        : first.UnlockWithPassphraseAsync(foreignCredential));
            }
            finally { CryptographicOperations.ZeroMemory(foreignKey); }

            await File.WriteAllTextAsync(headerPath, originalHeader);
            var restoredKey = recovery
                ? await first.UnlockWithRecoveryPhraseAsync(firstCreated.Recovery)
                : await first.UnlockWithPassphraseAsync(TestVault.Passphrase);
            try { Assert.Equal(firstKey, restoredKey); }
            finally { CryptographicOperations.ZeroMemory(restoredKey); }
        }
        finally
        {
            if (firstKey is not null) CryptographicOperations.ZeroMemory(firstKey);
            if (secondKey is not null) CryptographicOperations.ZeroMemory(secondKey);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlock_WhenCredentialIsWrong_PreservesAuthenticationFailure(bool recovery)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".vault-key-check-" + Guid.NewGuid().ToString("N"));
        var vault = new VaultStore(root);
        byte[]? key = null;
        try
        {
            var created = await vault.CreateAsync(TestVault.Passphrase);
            key = created.Key;
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
                recovery
                    ? vault.UnlockWithRecoveryPhraseAsync("not the recovery phrase")
                    : vault.UnlockWithPassphraseAsync("not the passphrase"));
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
