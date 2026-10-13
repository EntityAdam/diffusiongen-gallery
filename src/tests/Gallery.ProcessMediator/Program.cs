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
