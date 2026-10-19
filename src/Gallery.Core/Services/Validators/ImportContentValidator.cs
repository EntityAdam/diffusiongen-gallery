using System.Security.Cryptography;

namespace Gallery.Core.Services.Validators;

internal static class ImportContentValidator
{
    private const int ReadBufferSize = 81920;

    public static Task<MemoryStream> ReadAsync(Stream stream, long limit, string exceededLimitMessage) =>
        ReadOwnedContentAsync(stream, limit, exceededLimitMessage);

    private static async Task<MemoryStream> ReadOwnedContentAsync(Stream stream, long limit, string exceededLimitMessage)
    {
        var memoryStream = new MemoryStream();
        var readBufferBytes = new byte[ReadBufferSize];
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(readBufferBytes)) > 0)
            {
                if (memoryStream.Length + bytesRead > limit)
                    throw new InvalidOperationException(exceededLimitMessage);
                await memoryStream.WriteAsync(readBufferBytes.AsMemory(0, bytesRead));
            }
            return memoryStream;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(memoryStream.GetBuffer());
            memoryStream.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(readBufferBytes); }
    }
}
