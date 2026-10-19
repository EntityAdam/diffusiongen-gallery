using Gallery.Core.Services.Processors.Codecs;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Gallery.Tests;

internal static class DuplicateCacheArrangement
{
    public static async Task ArrangeAsync(TestVault fixture, IReadOnlyList<(ImageRecord ImageRecord, ulong Hash)> records)
    {
        foreach (var (record, _) in records)
            await fixture.RawStore.UpdateAsync(record.Id, VaultCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(record), fixture.Session.Key, $"record:{record.Id}"));
        var cachedHashes = records.ToDictionary(item => item.ImageRecord.Id, item => new { item.ImageRecord.PngSha256, item.Hash });
        var payload = VaultCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(cachedHashes), fixture.Session.Key, "perceptual-hashes:v1");
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings(id,payload) VALUES('perceptual-hashes',$payload)";
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }
}
