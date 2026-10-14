using Gallery.Web.Services.Processors;
using Gallery.Web.Services.DataManagers;
using System.Security.Cryptography;
using Gallery.Web.Services;
using Microsoft.Data.Sqlite;


namespace Gallery.Tests;

// ContentFingerprint is the production oracle used in the acceptance migration test
// to query the independently observed fingerprint index. These tests license that use and pin
// key/domain separation, case normalization, and malformed-input behavior unavailable in gallery outputs.
[Trait("Category", "class_test")]
public sealed class TestContentFingerprintOracle
{
    [Fact]
    public void Fingerprint_WhenHashIsValid_IsDeterministicVaultSpecificAndDomainSeparated()
    {
        // Arrange
        var key = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData("source image"u8);

        // Act
        var fingerprint = VaultCrypto.ContentFingerprint(key, Convert.ToHexString(hash));

        // Assert
        Assert.Equal(32, fingerprint.Length);
        Assert.Equal(fingerprint, VaultCrypto.ContentFingerprint(key, Convert.ToHexString(hash).ToLowerInvariant()));
        Assert.NotEqual(fingerprint, VaultCrypto.ContentFingerprint(RandomNumberGenerator.GetBytes(32), Convert.ToHexString(hash)));
        Assert.NotEqual(fingerprint, HMACSHA256.HashData(key, hash));
        Assert.NotEqual(hash, fingerprint);

    }

    [Fact]
    public void Fingerprint_WhenHashIsMalformed_ThenIdentifiesItsValueAndRequiredFormat()
    {
        // Arrange
        var key = RandomNumberGenerator.GetBytes(32);
        const string malformedHash = "AB";

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            VaultCrypto.ContentFingerprint(key, malformedHash));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(new ExpectedException("invalid source fingerprints identify the offending value and expected hash format", malformedHash, "SHA-256", "64"), exception);
    }


}
