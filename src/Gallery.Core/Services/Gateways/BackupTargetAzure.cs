using Azure.Storage.Blobs;
using Azure.Core.Pipeline;

namespace Gallery.Core.Services.Gateways;

internal sealed class BackupTargetAzure : IBackupTarget
{
    private readonly BlobContainerClient blobContainerClient;
    private readonly string prefix;

    internal BackupTargetAzure(string connectionString, string containerName, string prefix, HttpClient? httpClient = null)
    {
        blobContainerClient = CreateClient(connectionString, httpClient).GetBlobContainerClient(containerName);
        this.prefix = prefix;
    }

    internal BackupTargetAzure(string sasUrl, string prefix = "", HttpClient? httpClient = null)
    {
        blobContainerClient = new BlobContainerClient(ParseContainerSasUrl(sasUrl), CreateOptions(httpClient));
        this.prefix = prefix;
    }

    public static void ValidateConnectionString(string connectionString) => _ = CreateClient(connectionString);

    private static BlobClientOptions CreateOptions(HttpClient? httpClient)
    {
        var options = new BlobClientOptions();
        if (httpClient is not null) options.Transport = new HttpClientTransport(httpClient);
        return options;
    }

    private static BlobServiceClient CreateClient(string connectionString, HttpClient? httpClient = null)
    {
        try { return new BlobServiceClient(connectionString, CreateOptions(httpClient)); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The Azure Storage connection string is invalid.", exception);
        }
    }

    public void ValidateArchiveName(string name) => Validators.BackupArchiveNameValidator.Validate(name);

    private string BlobName(string name)
    {
        ValidateArchiveName(name);
        return prefix.Length == 0 ? name : $"{prefix}/{name}";
    }
    private BlobClient Blob(string name) => blobContainerClient.GetBlobClient(BlobName(name));

    public async Task<IReadOnlyList<string>> ListAsync()
    {
        var names = await ReadTopLevelArchiveNamesAsync();
        return OrderArchiveNames(names);
    }

    private async Task<List<string>> ReadTopLevelArchiveNamesAsync()
    {
        var names = new List<string>();
        var blobPrefix = prefix.Length == 0 ? "" : prefix + "/";
        await foreach (var blob in blobContainerClient.GetBlobsAsync(prefix: blobPrefix))
        {
            var name = blob.Name[blobPrefix.Length..];
            if (IsTopLevelArchiveName(name)) names.Add(name);
        }
        return names;
    }

    private static bool IsTopLevelArchiveName(string name) =>
        !name.Contains('/') && name.EndsWith(".zip", StringComparison.Ordinal);

    private static string[] OrderArchiveNames(IEnumerable<string> names) =>
        names.OrderDescending(StringComparer.Ordinal).ToArray();

    public async Task<bool> ExistsAsync(string name) =>
        (await Blob(name).ExistsAsync()).Value;

    public async Task DownloadAsync(string name, string destinationPath) =>
        await Blob(name).DownloadToAsync(destinationPath);

    public async Task UploadAsync(string name, string sourcePath) =>
        await Blob(name).UploadAsync(sourcePath, overwrite: false);
    private static Uri ParseContainerSasUrl(string sasUrl)
    {
        if (!Uri.TryCreate(sasUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Enter an HTTPS Azure Blob container SAS URL.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1)
            throw new InvalidOperationException("The SAS URL must address a container, not a storage account or an individual blob.");
        var queryValuesByParameterName = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .GroupBy(part => Uri.UnescapeDataString(part[0].Replace('+', ' ')), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => string.Join(',', group.Select(part => part.Length == 2
                    ? Uri.UnescapeDataString(part[1].Replace('+', ' ')) : "")), StringComparer.OrdinalIgnoreCase);
        if (!queryValuesByParameterName.TryGetValue("sr", out var resource) || resource != "c"
            || !queryValuesByParameterName.TryGetValue("sig", out var signature) || string.IsNullOrWhiteSpace(signature)
            || !queryValuesByParameterName.TryGetValue("sp", out var permissions)
            || !permissions.ToString().Contains('r') || !permissions.ToString().Contains('l'))
            throw new InvalidOperationException("Use a container-level SAS URL with read and list permissions.");
        return uri;
    }
}
