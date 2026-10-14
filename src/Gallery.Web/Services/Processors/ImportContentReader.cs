using System.Security.Cryptography;

namespace Gallery.Web.Services.Processors;

internal static class ImportContentReader
{
    private const int ReadBufferSize = 81920;

    public static Task<MemoryStream> ReadAsync(Stream input, long limit, string exceededLimitMessage) =>
        ReadOwnedContentAsync(input, limit, exceededLimitMessage);

    private static async Task<MemoryStream> ReadOwnedContentAsync(Stream input, long limit, string exceededLimitMessage)
    {
        var content = new MemoryStream();
        var buffer = new byte[ReadBufferSize];
        try
        {
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer)) > 0)
            {
                if (content.Length + bytesRead > limit)
                    throw new InvalidOperationException(exceededLimitMessage);
                await content.WriteAsync(buffer.AsMemory(0, bytesRead));
            }
            return content;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(content.GetBuffer());
            content.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
