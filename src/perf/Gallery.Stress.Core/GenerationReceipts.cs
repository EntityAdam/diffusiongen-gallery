using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gallery.Stress.Core;

// Receipts live in the owned workspace, outside the source folder. A receipt only saves generation
// work: every reused file is hashed again, and production import still verifies encrypted media.
public sealed class GenerationReceipts : IAsyncDisposable
{
    private sealed record Entry(string Configuration, long Index, long Bytes, string Sha256);
    private readonly Dictionary<long, Entry> entries;
    private readonly string configuration;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int unflushed;

    private GenerationReceipts(Dictionary<long, Entry> entries, string configuration, StreamWriter writer)
    {
        this.entries = entries;
        this.configuration = configuration;
        this.writer = writer;
    }

    public static async Task<GenerationReceipts> OpenAsync(string path, int seed, long start, int count,
        string sizes, string metadata, CancellationToken token)
    {
        var configuration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version = 1, seed, start, count, sizes, metadata }))));
        var entries = new Dictionary<long, Entry>();
        if (File.Exists(path))
        {
            using var reader = File.OpenText(path);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                Entry? entry;
                try { entry = JsonSerializer.Deserialize<Entry>(line); }
                catch (JsonException) { continue; } // A hard kill can leave a partial final append.
                if (entry is null) continue;
                if (entry.Configuration != configuration)
                    throw new InvalidOperationException("Generation receipts differ from the saved build configuration.");
                if (entry.Index >= start && entry.Index < start + count && entry.Bytes > 0 &&
                    entry.Sha256 is { Length: 64 } && entry.Sha256.All(Uri.IsHexDigit))
                    entries[entry.Index] = entry;
            }
        }
        var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        // Separate a previous partial record from future appends.
        await writer.WriteLineAsync();
        return new(entries, configuration, writer);
    }

    public async Task<long?> TryReuseAsync(long index, string path, CancellationToken token)
    {
        // Entries are loaded once, then immutable; newly written entries are only needed on restart.
        if (!entries.TryGetValue(index, out var entry) || !File.Exists(path)) return null;
        await using var file = File.OpenRead(path);
        if (file.Length != entry.Bytes) return null;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        return hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase) ? entry.Bytes : null;
    }

    public async Task RecordAsync(long index, GeneratedPng generated, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Entry(configuration, index, generated.Bytes, generated.Sha256)));
            if (++unflushed >= 64)
            {
                await writer.FlushAsync(token);
                unflushed = 0;
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await writer.DisposeAsync();
        gate.Dispose();
    }
}
