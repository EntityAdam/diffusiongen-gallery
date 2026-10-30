if (args is ["interrupt-creation", var creationRoot, var phase])
{
    var staging = Path.Combine(creationRoot, ".vault-create-" + Guid.NewGuid().ToString("N"));
    using (var service = Gallery.Core.Services.GalleryService.Create(staging,
        new Gallery.Core.Models.GalleryRuntimeOptions { StateDirectory = Path.Combine(staging, "state") },
        new HttpClient()))
    {
        const string passphrase = "a strong unique test passphrase";
        await service.CreateVaultAsync(passphrase, passphrase, "Interrupted creation");
        await service.CloseVaultAsync();
    }
    // Arrange the exact durable publication prefix, then let the parent terminate this process.
    var manager = typeof(Gallery.Core.Services.GalleryService).Assembly.GetType("Gallery.Core.Services.VaultManager")!;
    manager.GetMethod("PrepareCreationPublication", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .Invoke(null, [staging, creationRoot]);
    if (phase is "catalog" or "header")
        File.Move(Path.Combine(staging, "gallery.db"), Path.Combine(creationRoot, "gallery.db"));
    if (phase == "header")
        File.Move(Path.Combine(staging, "vault.json"), Path.Combine(creationRoot, "vault.json"));
    Console.WriteLine("ready");
    await Console.In.ReadLineAsync();
    return;
}

if (args is ["checkout", var root])
{
    Directory.CreateDirectory(root);
    using var checkout = new FileStream(Path.Combine(root, ".vault-checkout"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Console.WriteLine("ready");
    await Console.In.ReadLineAsync();
    return;
}

// The process transport supplies controlled provider bytes. The production gateway, parser,
// import validation, encryption and persistence remain in the application under test.
var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
var payloadName = executableName == "ffprobe" ? "probe.json" : "frame.png";
await using var payload = File.OpenRead(Path.Combine(AppContext.BaseDirectory, payloadName));
await payload.CopyToAsync(Console.OpenStandardOutput());
