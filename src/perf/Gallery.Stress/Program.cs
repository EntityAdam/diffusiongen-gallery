using System.Text.Json;
using Gallery.Stress;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); Console.Error.WriteLine("Stopping at the next safe boundary; rerun with the same options to resume."); };
try
{
    var options = new StressOptions(args);
    using var workspaceLock = LockWorkspace(options);
    switch (options.Command)
    {
        case "build": await BuildCommand.RunAsync(options, cancellation.Token); break;
        case "cleanup": await StressResources.CleanupAsync(options, cancellation.Token); break;
        case "measure-open": await MeasureOpenCommand.RunAsync(options, cancellation.Token); break;
        case "open-once":
            Console.WriteLine(JsonSerializer.Serialize(await OpenOnceCommand.RunAsync(options, cancellation.Token), ResultWriter.JsonOptions)); break;
        case "profile-catalog":
            Safety.ValidateOutputPath(options.Workspace);
            var writer = new ResultWriter(options.Workspace);
            var profile = await CatalogProfiler.ProfileAsync(options.Required("db"), cancellation.Token);
            await writer.AppendAsync("catalog-profile", new { env = writer.Environment, profile }, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(profile, ResultWriter.JsonOptions)); break;
        case "simulate-catalog": await SimulateCatalogCommand.RunAsync(options, cancellation.Token); break;
        case "large-images": await LargeImageSuiteCommand.RunAsync(options, cancellation.Token); break;
        case "large-ingest-once": await LargeImageSuiteCommand.IngestOnceAsync(options, cancellation.Token); break;
        case "viewer-once": await LargeImageSuiteCommand.ViewerOnceAsync(options, cancellation.Token); break;
        case "report": await ReportCommand.RunAsync(options, cancellation.Token); break;
        case "help": case "--help": case "-h":
            Console.WriteLine("""
                Gallery.Stress — separate, opt-in gallery performance harness
                Commands (all require --workspace <outside-repository-directory>):
                  profile-catalog --db <gallery.db>       Read-only catalog distributions
                  build --vault <dir> --staging <dir>     Generate and ingest; resumable
                    [--targets 100k,200k,400k --batch-files 10000 --size-dist fixed:1MB]
                    [--seed 42 --workers 4 --catalog-batch 16 --gen-threads N]
                    [--metadata-dist uniform:8KB-60KB --media-directory <dir>]
                    [--measure-open-at-targets --open-runs 3 --cache both]
                  measure-open --vault <dir> [--runs 5 --cache cold|warm|both --page-size <count>]
                    [--label <name> --wait-for-drive-idle]
                    [--allow-real-vault --passphrase-file <file>] Explicit real-vault measurement
                  simulate-catalog --vault <dir> [--rows 500k,1M,2.1M --sparse-media]
                  large-images [--tiers L1,L2,L3,L4,L5 --images-per-tier 50 --decouple]
                  report                                  Markdown, CSV and plots
                  cleanup [--delete-vault]                 Marker-protected cleanup
                See src/perf/Gallery.Stress/README.md for operational guidance.
                """); break;
        default: throw new ArgumentException("Unknown command. Use --help.");
    }
    return 0;
}
catch (OperationCanceledException) { return 130; }
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}

static FileStream? LockWorkspace(StressOptions options)
{
    if (options.Command is not ("build" or "cleanup" or "simulate-catalog" or "large-images" or "measure-open")) return null;
    Safety.ValidateOutputPath(options.Workspace);
    Directory.CreateDirectory(options.Workspace);
    var path = Path.Combine(options.Workspace, ".harness.lock");
    Safety.ValidateOutputPath(path);
    try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { throw new InvalidOperationException("Another mutating harness command owns this workspace."); }
}
