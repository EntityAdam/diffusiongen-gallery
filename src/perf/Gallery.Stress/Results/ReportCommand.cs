using System.Globalization;
using System.Text;
using System.Text.Json;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

namespace Gallery.Stress;

internal static class ReportCommand
{
    public static async Task RunAsync(StressOptions options, CancellationToken token)
    {
        Safety.ValidateOutputPath(options.Workspace);
        var directory = Path.Combine(options.Workspace, "results");
        Directory.CreateDirectory(directory);
        var open = await ReadAsync(directory, "open.jsonl", token);
        var ingestion = await ReadAsync(directory, "ingestion.jsonl", token);
        var large = await ReadAsync(directory, "large-image.jsonl", token);
        var profiles = await ReadAsync(directory, "catalog-profile.jsonl", token);
        var failures = await ReadAsync(directory, "open-failures.jsonl", token);
        // A build can die between appending a batch and committing state. Prefer the latest result for that batch.
        ingestion = ingestion.GroupBy(row => (EnvironmentKey(row), Number(row, "batch")))
            .Select(group => group.LastOrDefault(row => Property(row, "report").ValueKind == JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } measured
                ? measured : group.Last()).ToList();
        var markdown = new StringBuilder("# Gallery stress measurements\n\n");
        markdown.AppendLine("Durations are milliseconds. Schema 3 measures first-page opening through the encrypted browse index, before thumbnails; catalog loading is optional background work. Schema 2 measures full-catalog readiness before thumbnails. Legacy rows measure startup through thumbnail completion. Browser rendering is excluded. Failed cache purges are a separate series and cannot establish cold-cache performance. Simulated vaults contain synthetic catalog records and may have unreadable placeholder media.\n");
        markdown.AppendLine("## Environments\n\n| Key | OS | Runtime | CPUs | Available memory bytes | Schema | GC mode | GC latency |\n|---|---|---|---:|---:|---|---|---|");
        foreach (var group in open.Concat(ingestion).Concat(large).Concat(failures).GroupBy(EnvironmentKey))
        {
            var env = Property(group.First(), "env");
            markdown.AppendLine($"| {group.Key} | {Cell(Text(env, "os"))} | {Cell(Text(env, "dotnet"))} | {F(Number(env, "cpus"))} | {F(Number(env, "ramBytes"))} | {Cell(Text(env, "schemaVersion"))} | {Cell(Text(env, "gcMode"))} | {Cell(Text(env, "gcLatencyMode"))} |");
        }
        markdown.AppendLine("\nSeries are separated by measurement schema, effective GC configuration, page size, optional background/drive waits, environment, cache status and simulated/real data. Legacy GC mode is unknown and is never inferred or merged with recorded server/workstation modes. Available memory is the runtime's memory budget, which may differ from installed RAM. Drive kinds, power state, exclusions and commit are included in the environment key when present.\n");
        markdown.AppendLine("![Measured open times](open.svg)\n\n![Open steps by catalog records](open-steps-records.svg)\n\n![Open steps by catalog bytes](open-steps-catalog-bytes.svg)\n");
        if (profiles.Count > 0)
        {
            markdown.AppendLine("## Catalog calibration\n\nProfile distributions are in catalog-profile.jsonl. This read-only profile includes encrypted blob sizes; it does not measure plaintext metadata size. Use it to calibrate the synthetic record shape before trusting extrapolations.\n\n| Rows | Record p50 bytes | Record p95 bytes | Record max bytes | Thumbnail p50 bytes | Thumbnail p95 bytes | Catalog file bytes | WAL bytes |\n|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var row in profiles)
            {
                var profile = Property(row, "profile").ValueKind == JsonValueKind.Object ? Property(row, "profile") : row;
                var record = Property(profile, "record");
                var thumbnail = Property(profile, "thumbnail");
                markdown.AppendLine($"| {F(Number(profile, "rows"))} | {F(Number(record, "p50"))} | {F(Number(record, "p95"))} | {F(Number(record, "max"))} | {F(Number(thumbnail, "p50"))} | {F(Number(thumbnail, "p95"))} | {F(Number(profile, "fileBytes"))} | {F(Number(profile, "walBytes"))} |");
            }
            markdown.AppendLine();
        }
        var openCsv = new StringBuilder("series,label,run,pid,records,catalog_bytes,record_bytes,wal_bytes,step,milliseconds\n");
        var modelCsv = new StringBuilder("series,step,predictor,samples,distinct_sizes,intercept_ms,slope,r_squared,target_rows,predictor_at_target,prediction_ms,pi95_low_ms,pi95_high_ms\n");
        var series = open.Where(row => Property(row, "steps").ValueKind == JsonValueKind.Object).GroupBy(Series).OrderBy(group => group.Key).ToArray();
        var fits = new List<SeriesFit>();
        markdown.AppendLine("## Open measurements\n");
        if (series.Length == 0) markdown.AppendLine("No completed open measurements.\n");
        foreach (var group in series)
        {
            var rows = group.ToArray();
            markdown.AppendLine($"Timing target: `{MeasurementSeries.OpenStep(rows[0])}`.\n");
            markdown.AppendLine($"### {Cell(group.Key)}\n\n| Label | Records | Catalog bytes (median) | Runs | Open min | Median | Max |\n|---|---:|---:|---:|---:|---:|---:|");
            foreach (var target in rows.GroupBy(row => (Label: Text(row, "label"), Records: Number(row, "records"))).OrderBy(group => group.Key.Records))
            {
                var totals = target.Select(MeasurementSeries.OpenMilliseconds).ToArray();
                markdown.AppendLine($"| {Cell(target.Key.Label)} | {F(target.Key.Records)} | {F(Median(target.Select(row => Number(row, "catalogBytes"))))} | {target.Count()} | {F(totals.Min())} | {F(Median(totals))} | {F(totals.Max())} |");
            }
            markdown.AppendLine("\n| Records | Step | Median ms | p95 ms |\n|---:|---|---:|---:|");
            foreach (var target in rows.GroupBy(row => Number(row, "records")).OrderBy(group => group.Key))
            foreach (var step in StepNames(target))
            {
                var values = target.Where(row => HasStep(row, step)).Select(row => Step(row, step)).Order().ToArray();
                markdown.AppendLine($"| {F(target.Key)} | {Cell(step)} | {F(Median(values))} | {F(Quantile(values, .95))} |");
            }
            markdown.AppendLine("\nLeast-squares models: `t = intercept + slope × predictor`. Three distinct measured sizes are required. Prediction intervals assume independent errors of constant variance and a linear relationship; memory exhaustion and cache changes can invalidate them. Lower limits are clamped to zero.\n\n| Step | Predictor | Samples | Intercept ms | Slope | R² | Extrapolated 1M ms (95% PI) | Extrapolated 2M ms (95% PI) |\n|---|---|---:|---:|---:|---:|---|---|");
            foreach (var step in StepNames(rows))
            foreach (var predictor in new[] { "records", "record_bytes", "catalog_bytes" })
            {
                var points = rows.Where(row => HasStep(row, step)).Select(row => (X: Predictor(row, predictor), Y: Step(row, step))).Where(point => point.X > 0).ToArray();
                var fit = rows.Select(row => Number(row, "records")).Distinct().Count() < 3 ? null : Fit.Create(points);
                if (fit is null)
                {
                    markdown.AppendLine($"| {Cell(step)} | {predictor} | {points.Length} | — | — | — | insufficient sizes | insufficient sizes |");
                    continue;
                }
                var bytesPerRecord = predictor == "records" ? 1 : Median(rows.Where(row => Number(row, "records") > 0 && Predictor(row, predictor) > 0)
                    .Select(row => Predictor(row, predictor) / Number(row, "records")));
                var first = fit.Predict(1_000_000 * bytesPerRecord);
                var second = fit.Predict(2_000_000 * bytesPerRecord);
                markdown.AppendLine($"| {Cell(step)} | {predictor} | {fit.N} | {F(fit.Intercept)} | {G(fit.Slope)} | {F(fit.RSquared)} | {Interval(first)} | {Interval(second)} |");
                foreach (var target in new[] { 1_000_000, 2_000_000, 2_100_000 })
                {
                    var x = target * bytesPerRecord;
                    var prediction = fit.Predict(x);
                    Csv(modelCsv, group.Key, step, predictor, fit.N, fit.Distinct, fit.Intercept, fit.Slope, fit.RSquared, target,
                        x, prediction.Value, prediction.Low, prediction.High);
                }
                fits.Add(new(group.Key, rows[0], step, predictor, fit));
            }
            markdown.AppendLine("\nByte-model extrapolations use the median measured bytes per record; they assume record shape stays constant. The CSV also includes the 2.1M-record (approximately 5 TB of real media) estimate. No UI-rendering prediction is implied.\n");
            foreach (var row in rows)
            foreach (var step in StepNames([row]))
                Csv(openCsv, group.Key, Text(row, "label"), Number(row, "run"), Number(row, "pid"), Number(row, "records"),
                    Number(row, "catalogBytes"), RecordBytes(row), Number(row, "walBytes"), step, Step(row, step));
        }
        AddMemoryAndFailures(markdown, open, failures);
        AddHypotheses(markdown, open);
        AddAgreement(markdown, fits);
        var ingestionCsv = new StringBuilder("environment,batch,cumulative_files,cumulative_media_bytes,catalog_bytes,generation_ms,files_per_second,mib_per_second,stage,count,p50_ms,p95_ms,max_ms\n");
        AddIngestion(markdown, ingestionCsv, ingestion);
        var largeCsv = new StringBuilder("environment,tier,workers,bytes,pixels,stage,p50_ms,p95_ms,max_ms\n");
        AddLargeImages(markdown, largeCsv, large);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), markdown.ToString(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "open.csv"), openCsv.ToString(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "models.csv"), modelCsv.ToString(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "ingestion.csv"), ingestionCsv.ToString(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "large-image.csv"), largeCsv.ToString(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "open.svg"), ScatterPlot(open.Select(row => (Series(row), Number(row, "records"), MeasurementSeries.OpenMilliseconds(row))), "Open time (ms; target identified by series)", "Catalog records"), token);
        foreach (var predictor in new[] { "records", "catalog-bytes" })
        {
            var points = open.Where(row => Property(row, "steps").ValueKind == JsonValueKind.Object)
                .SelectMany(row => StepNames([row]).Select(step => (Series(row) + " / " + step, Number(row, predictor == "records" ? "records" : "catalogBytes"), Step(row, step))));
            await File.WriteAllTextAsync(Path.Combine(directory, $"open-steps-{predictor}.svg"), ScatterPlot(points, "Open steps (ms)", predictor == "records" ? "Catalog records" : "Catalog bytes"), token);
        }
        foreach (var measure in new[] { "files-per-second", "mib-per-second", "stage-p95" })
        {
            var points = ingestion.SelectMany(row =>
            {
                var report = Property(row, "report");
                var seconds = Number(report, "wallMs") / 1000;
                var seriesName = EnvironmentKey(row) + " / workers " + F(Number(report, "workers"));
                var x = Number(row, "cumulativeMediaBytes");
                return measure == "stage-p95"
                    ? Array(Property(report, "stages")).Select(stage => (seriesName + " / " + Text(stage, "stage"), x, Number(stage, "p95Ms")))
                    : new[] { (seriesName, x, seconds <= 0 ? 0 : measure == "files-per-second" ? Number(report, "imported") / seconds : Number(report, "sourceBytes") / 1048576 / seconds) };
            });
            await File.WriteAllTextAsync(Path.Combine(directory, $"ingestion-{measure}.svg"), ScatterPlot(points, measure == "stage-p95" ? "Ingestion stage p95 (ms)" : measure, "Cumulative media bytes"), token);
        }
        Console.WriteLine("Wrote report.md, CSV tables and open.svg in the workspace results directory.");
    }

    private static void AddMemoryAndFailures(StringBuilder text, List<JsonElement> open, List<JsonElement> failures)
    {
        text.AppendLine("## Memory and failure points\n\n| Series | Records | Runs | Peak resident bytes | Peak private bytes | Allocated bytes | GC pause ms | Managed bytes at end | Gen 0 | Gen 1 | Gen 2 | Read bytes | Read operations |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in open.GroupBy(row => (Series: Series(row), Records: Number(row, "records"))).OrderBy(group => group.Key.Records))
        {
            string Top(string key, bool positive = false) => MeasurementSeries.MedianAvailable(group.Select(row => Property(row, key)), positive);
            string Gc(string key) => MeasurementSeries.MedianAvailable(group.Select(row => Property(Property(row, "gc"), key)));
            string Io(string key) => MeasurementSeries.MedianAvailable(group.Select(row => Property(Property(row, "io"), key)));
            text.AppendLine($"| {Cell(group.Key.Series)} | {F(group.Key.Records)} | {group.Count()} | {Top("peakWorkingSetBytes", true)} | {Top("peakPrivateBytes", true)} | {Gc("allocatedBytes")} | {Gc("pauseMs")} | {Gc("managedBytesAtEnd")} | {Gc("gen0")} | {Gc("gen1")} | {Gc("gen2")} | {Io("readBytes")} | {Io("readOps")} |");
        }
        text.AppendLine("\nValues are medians. — means unavailable; legacy zero peak-memory readings are unavailable. A supported zero read count remains zero. Allocated bytes are cumulative traffic, not peak/live memory. Managed bytes at end are an uncollected heap estimate; last-GC heap snapshots are recorded separately in JSON. GC pause time overlaps operation timings and must not be added to them. Memory/I/O scope includes thumbnails and optional drive-idle wait. Memory exhaustion is a cliff, so a linear timing model does not establish feasibility above the largest successful run.\n");
        if (failures.Count == 0)
        {
            text.AppendLine("No recorded open failures. This does not establish the memory ceiling.\n");
            return;
        }
        text.AppendLine("| Series | Target label | Run | Records if known | Catalog bytes | Error type | Exit code |\n|---|---|---:|---|---|---|---| ");
        foreach (var row in failures)
            text.AppendLine($"| {Cell(Series(row))} | {Cell(Text(row, "label"))} | {F(Number(row, "run"))} | {Cell(Text(row, "records"))} | {Cell(Text(row, "catalogBytes"))} | {Cell(Text(row, "errorType"))} | {Cell(Text(row, "exitCode"))} |");
        text.AppendLine("\nA failed child supplies a failure point, not a valid timing sample. Exit/error types alone do not prove an out-of-memory cause. Inspect external process diagnostics before classifying it. Extrapolations crossing a failed size are unvalidated.\n");
    }

    private static void AddHypotheses(StringBuilder text, List<JsonElement> open)
    {
        text.AppendLine("## Bottleneck evidence (H1–H6)\n\nEvidence is evaluated within each compatible series; GC modes and legacy/new timing targets are never pooled. Correlation alone does not establish cause.\n");
        if (open.Count == 0) AddHypothesesForSeries(text, open);
        foreach (var group in open.GroupBy(Series))
        {
            text.AppendLine($"### {Cell(group.Key)}\n");
            AddHypothesesForSeries(text, group.ToList());
        }
    }

    private static void AddHypothesesForSeries(StringBuilder text, List<JsonElement> open)
    {
        text.AppendLine("| Hypothesis | Status | Evidence |\n|---|---|---|");
        var measured = open.Where(row => MeasurementSeries.OpenMilliseconds(row) > 0).ToArray();
        if (measured.Length == 0)
        {
            foreach (var id in Enumerable.Range(1, 6)) text.AppendLine($"| H{id} | inconclusive | No completed open runs. |");
            return;
        }
        var decrypt = Median(measured.Select(row => (Telemetry(row, "gallery.load.decrypt_ms") + Telemetry(row, "gallery.load.deserialize_ms")) / MeasurementSeries.OpenMilliseconds(row)));
        var hasLoad = measured.All(row => HasTelemetry(row, "gallery.load.decrypt_ms") && HasTelemetry(row, "gallery.load.deserialize_ms"));
        text.AppendLine($"| H1: decrypt + JSON dominates | {(hasLoad && decrypt >= .5 ? "supported" : hasLoad && decrypt < .1 ? "rejected" : "inconclusive")} | Median share of the series opening target: {F(decrypt * 100)}%. |");
        var pairs = measured.GroupBy(row => (EnvironmentKey(row), Simulated(row), Number(row, "records"))).Select(group =>
        {
            var cold = group.Where(row => Text(row, "cache") == "cold" && Text(row, "cachePurge") == "standby-list").ToArray();
            var warm = group.Where(row => Text(row, "cache") == "warm").ToArray();
            return (Cold: cold, Warm: warm);
        }).Where(pair => pair.Cold.Length > 0 && pair.Warm.Length > 0).ToArray();
        var gap = pairs.Length == 0 ? double.NaN : Median(pairs.Select(pair => Median(pair.Cold.Select(row => MeasurementSeries.OpenMilliseconds(row))) /
            Math.Max(.001, Median(pair.Warm.Select(row => MeasurementSeries.OpenMilliseconds(row)))) - 1));
        text.AppendLine($"| H2: cold I/O dominates | {(double.IsFinite(gap) && gap > .5 ? "supported" : "inconclusive")} | {(double.IsFinite(gap) ? $"Median matched cold/warm time difference: {F(gap * 100)}%; confirm with I/O counters and query_read_ms." : "No matched verified cold and warm samples at the same size and environment.")} |");
        var migration = Median(measured.Select(row => MetricSum(row, "gallery.catalog.migration.duration") / MeasurementSeries.OpenMilliseconds(row)));
        text.AppendLine($"| H3: connection/migration overhead | {(migration > .25 ? "supported" : "inconclusive")} | Whole-run migration sum / opening target: {F(migration * 100)}%; background work may overlap, so this is not an exclusive share. |");
        var hasKdf = measured.All(row => HasTelemetry(row, "vault.unlock.kdf_ms"));
        var kdf = measured.Select(row => Telemetry(row, "vault.unlock.kdf_ms")).ToArray();
        text.AppendLine($"| H4: constant KDF cost | inconclusive | {(hasKdf ? $"KDF median {F(Median(kdf))} ms, range {F(kdf.Min())}–{F(kdf.Max())} ms. Compare multiple sizes within each environment; fixed-cost causality requires controlled runs." : "KDF telemetry is missing.")} |");
        var idle = measured.Where(row => HasStep(row, "drive_idle")).Select(row => Step(row, "drive_idle")).ToArray();
        text.AppendLine($"| H5: background I/O contention | inconclusive | {(idle.Length == 0 ? "Drive-idle measurements absent." : $"Median post-thumbnail drive wait {F(Median(idle))} ms.")} A controlled comparison with/without pending work is required. |");
        var backfilled = measured.Sum(row => Telemetry(row, "gallery.load.backfill_generation_count"));
        var backfill = Median(measured.Select(row => Telemetry(row, "gallery.load.backfill_generation_ms") / MeasurementSeries.OpenMilliseconds(row)));
        text.AppendLine($"| H6: stale generation migration | {(backfilled > 0 && backfill > .5 ? "supported" : "inconclusive")} | {F(backfilled)} generation records backfilled across runs; median time share {F(backfill * 100)}%. Zero stale rows cannot test migration cost. |\n");
    }

    private static void AddAgreement(StringBuilder text, List<SeriesFit> fits)
    {
        text.AppendLine("## Real versus simulated agreement\n\nCompare row-count slopes of the primary opening target only within the same environment and cache status. A difference above 15% requires recalibrating synthetic record shape before trusting extrapolation.\n");
        var checks = 0;
        foreach (var real in fits.Where(fit => fit.Step == MeasurementSeries.OpenStep(fit.Row) && fit.Predictor == "records" && !Simulated(fit.Row)))
        foreach (var simulated in fits.Where(fit => fit.Step == real.Step && fit.Predictor == "records" && Simulated(fit.Row)
                     && EnvironmentKey(fit.Row) == EnvironmentKey(real.Row) && CacheKey(fit.Row) == CacheKey(real.Row)))
        {
            checks++;
            var difference = Math.Abs(real.Model.Slope - simulated.Model.Slope) / Math.Max(Math.Abs(real.Model.Slope), 1e-12);
            text.AppendLine($"- {Cell(real.Series)} versus {Cell(simulated.Series)}: slopes differ by {F(difference * 100)}% ({(difference <= .15 ? "within 15%" : "recalibration required")}).");
        }
        if (checks == 0) text.AppendLine("Inconclusive: both real and simulated series need at least three distinct measured sizes under matching conditions.\n");
    }

    private static void AddIngestion(StringBuilder text, StringBuilder csv, List<JsonElement> ingestion)
    {
        text.AppendLine("\n## Ingestion\n\n| Environment | Batch | Cumulative files | Cumulative media bytes | Files/s | MiB/s | Generation ms |\n|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in ingestion.OrderBy(row => Number(row, "cumulativeFiles")))
        {
            var report = Property(row, "report");
            var seconds = Number(report, "wallMs") / 1000;
            var files = seconds > 0 ? Number(report, "imported") / seconds : 0;
            var mib = seconds > 0 ? Number(report, "sourceBytes") / 1048576 / seconds : 0;
            text.AppendLine($"| {EnvironmentKey(row)} | {F(Number(row, "batch"))} | {F(Number(row, "cumulativeFiles"))} | {F(Number(row, "cumulativeMediaBytes"))} | {F(files)} | {F(mib)} | {F(Number(row, "generationMs"))} |");
            foreach (var stage in Array(Property(report, "stages")))
                Csv(csv, EnvironmentKey(row), Number(row, "batch"), Number(row, "cumulativeFiles"), Number(row, "cumulativeMediaBytes"),
                    Number(row, "catalogBytes"), Number(row, "generationMs"), files, mib, Text(stage, "stage"), Number(stage, "count"),
                    Number(stage, "p50Ms"), Number(stage, "p95Ms"), Number(stage, "maxMs"));
        }
        text.AppendLine("\n![Ingestion files per second](ingestion-files-per-second.svg)\n\n![Ingestion MiB per second](ingestion-mib-per-second.svg)\n\n![Ingestion stage p95](ingestion-stage-p95.svg)\n");
        text.AppendLine("\nPer-stage p50, p95 and max versus cumulative files/bytes are in ingestion.csv. Worker stage times can overlap. Retry rows are deduplicated by environment and batch, retaining the latest measured row when available. A recovered fully committed batch can have a null timing report.\n");
    }

    private static void AddLargeImages(StringBuilder text, StringBuilder csv, List<JsonElement> large)
    {
        text.AppendLine("## Large-image suite\n\n| Environment | Tier | Workers | Bytes | Pixels | Peak working set | Flags |\n|---|---|---:|---:|---:|---:|---|");
        foreach (var row in large)
        {
            var memory = Property(row, "memory");
            var peak = Number(memory, "peakWorkingSetBytes");
            var ram = Number(Property(row, "env"), "ramBytes");
            var flags = new List<string>();
            if (Property(row, "highMemory").ValueKind == JsonValueKind.True || ram > 0 && peak > ram * .5)
                flags.Add("peak memory >50% available RAM");
            if (Property(row, "slowOriginal").ValueKind == JsonValueKind.True || Array(Property(row, "viewer"))
                .Where(viewer => Text(viewer, "cache") == "warm")
                .Any(viewer => Number(Property(Property(viewer, "viewer"), "original"), "p50Ms") > 2000))
                flags.Add("warm original viewer >2 s");
            text.AppendLine($"| {EnvironmentKey(row)} | {Cell(Text(row, "tier"))} | {F(Number(row, "workers"))} | {F(Number(row, "bytes"))} | {F(Pixels(row))} | {MeasurementSeries.MedianAvailable([Property(memory, "peakWorkingSetBytes")], positiveOnly: true)} | {string.Join("; ", flags)} |");
            foreach (var stage in Array(Property(Property(row, "report"), "stages")))
                Csv(csv, EnvironmentKey(row), Text(row, "tier"), Number(row, "workers"), Number(row, "bytes"),
                    Pixels(row), Text(stage, "stage"), Number(stage, "p50Ms"), Number(stage, "p95Ms"), Number(stage, "maxMs"));
        }
        if (large.Count == 0)
        {
            text.AppendLine("\nNo large-image measurements.\n");
            return;
        }
        text.AppendLine("\n| Environment | Tier | Workers | Stage | p50 ms | p95 ms | Max ms |\n|---|---|---:|---|---:|---:|---:|");
        foreach (var row in large)
        foreach (var stage in Array(Property(Property(row, "report"), "stages")))
            text.AppendLine($"| {EnvironmentKey(row)} | {Cell(Text(row, "tier"))} | {F(Number(row, "workers"))} | {Cell(Text(stage, "stage"))} | {F(Number(stage, "p50Ms"))} | {F(Number(stage, "p95Ms"))} | {F(Number(stage, "maxMs"))} |");
        text.AppendLine("\n| Environment | Tier | Workers | Cache/purge | Viewer operation | Samples | p50 ms | p95 ms | Max ms |\n|---|---|---:|---|---|---:|---:|---:|---:|");
        foreach (var row in large)
        foreach (var viewer in Array(Property(row, "viewer")))
        {
            var operations = Property(viewer, "viewer");
            if (operations.ValueKind != JsonValueKind.Object) continue;
            foreach (var operation in operations.EnumerateObject())
                text.AppendLine($"| {EnvironmentKey(row)} | {Cell(Text(row, "tier"))} | {F(Number(row, "workers"))} | {Cell(CacheKey(viewer))} | {Cell(operation.Name)} | {F(Number(operation.Value, "count"))} | {F(Number(operation.Value, "p50Ms"))} | {F(Number(operation.Value, "p95Ms"))} | {F(Number(operation.Value, "maxMs"))} |");
        }
        text.AppendLine("\nLarge-image stage models fit each stage's p50 across tiers within the same environment and worker count. Three distinct sizes are required. Bytes and pixels usually rise together; use the decoupling pair to distinguish their effects.\n\n| Environment | Workers | Stage | Predictor | Samples | Intercept ms | Slope | R² |\n|---|---:|---|---|---:|---:|---:|---:|");
        foreach (var group in large.GroupBy(row => (Environment: EnvironmentKey(row), Workers: Number(row, "workers"))))
        {
            var points = group.SelectMany(row => Array(Property(Property(row, "report"), "stages"))
                .Select(stage => (Row: row, Stage: Text(stage, "stage"), Time: Number(stage, "p50Ms"))));
            foreach (var stage in points.GroupBy(point => point.Stage))
            foreach (var predictor in new[] { "bytes", "pixels" })
            {
                var fit = Fit.Create(stage.Select(point => (predictor == "bytes" ? Number(point.Row, "bytes") : Pixels(point.Row), point.Time)).ToArray());
                text.AppendLine($"| {group.Key.Environment} | {F(group.Key.Workers)} | {Cell(stage.Key)} | {predictor} | {stage.Count()} | {(fit is null ? "insufficient sizes" : F(fit.Intercept))} | {(fit is null ? "—" : G(fit.Slope))} | {(fit is null ? "—" : F(fit.RSquared))} |");
            }
        }
    }

    private static double Pixels(JsonElement row) => Number(row, "pixels") > 0 ? Number(row, "pixels") : Number(row, "width") * Number(row, "height");

    private static async Task<List<JsonElement>> ReadAsync(string directory, string name, CancellationToken token)
    {
        var rows = new List<JsonElement>();
        var file = Path.Combine(directory, name);
        if (!File.Exists(file)) return rows;
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(file, token))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { using var document = JsonDocument.Parse(line); rows.Add(document.RootElement.Clone()); }
            catch (JsonException)
            {
                Console.Error.WriteLine($"Ignoring incomplete/invalid {name} row {lineNumber}.");
            }
        }
        return rows;
    }

    private static string EnvironmentKey(JsonElement row) => MeasurementSeries.EnvironmentKey(row);
    private static string CacheKey(JsonElement row) => Text(row, "cache") + "/" + Text(row, "cachePurge");
    private static bool Simulated(JsonElement row) => Property(row, "simulated").ValueKind == JsonValueKind.True;
    private static string Series(JsonElement row) => $"{(Simulated(row) ? "simulated" : "real")} / {CacheKey(row)} / GC {Text(Property(row, "env"), "gcMode")} / {MeasurementSeries.OpenStep(row)} / env {EnvironmentKey(row)}";
    private static JsonElement Property(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) ? value : default;
    private static double Number(JsonElement row, string key) => Property(row, key).ValueKind == JsonValueKind.Number && Property(row, key).TryGetDouble(out var value) && double.IsFinite(value) ? value : 0;
    private static string Text(JsonElement row, string key) => Property(row, key).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "unknown" : Property(row, key).ToString();
    private static IEnumerable<JsonElement> Array(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
    private static IEnumerable<string> StepNames(IEnumerable<JsonElement> rows) => rows.SelectMany(row => Property(row, "steps").EnumerateObject().Select(property => property.Name)).Distinct().Order();
    private static bool HasStep(JsonElement row, string step) => Property(Property(row, "steps"), step).ValueKind == JsonValueKind.Number;
    private static double Step(JsonElement row, string step) => Number(Property(row, "steps"), step);
    private static bool HasTelemetry(JsonElement row, string tag) => Property(Property(row, "telemetry"), tag).ValueKind == JsonValueKind.Number;
    private static double Telemetry(JsonElement row, string tag) => Number(Property(row, "telemetry"), tag);
    private static double RecordBytes(JsonElement row) => Math.Max(Number(row, "recordBytes"), Telemetry(row, "gallery.load.record_bytes"));
    private static double MetricSum(JsonElement row, string instrument) => Number(Property(Property(row, "metrics"), instrument), "sum");
    private static double Predictor(JsonElement row, string predictor) => predictor switch { "records" => Number(row, "records"), "record_bytes" => RecordBytes(row), _ => Number(row, "catalogBytes") };
    private static double Median(IEnumerable<double> values) => Quantile(values.Order().ToArray(), .5);
    private static double Quantile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (sorted.Length - 1) * percentile;
        var low = (int)index;
        return sorted[low] + (sorted[(int)Math.Ceiling(index)] - sorted[low]) * (index - low);
    }
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string G(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");
    private static string Interval(Prediction prediction) => $"{F(prediction.Value)} ({F(prediction.Low)}–{F(prediction.High)})";
    private static void Csv(StringBuilder text, params object[] values) => text.AppendLine(string.Join(',', values.Select(value =>
    {
        var formatted = value is IFormattable numeric ? numeric.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "";
        return '"' + formatted.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    })));

    private static string ScatterPlot(IEnumerable<(string Series, double X, double Y)> points, string title, string xLabel)
    {
        var groups = points.Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .GroupBy(point => point.Series).ToArray();
        var maxX = Math.Max(1, groups.SelectMany(group => group).Select(point => point.X).DefaultIfEmpty(1).Max());
        var maxY = Math.Max(1, groups.SelectMany(group => group).Select(point => point.Y).DefaultIfEmpty(1).Max());
        var height = Math.Max(600, 510 + groups.Length * 17);
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000\" height=\"{height}\" viewBox=\"0 0 1000 {height}\"><rect width=\"1000\" height=\"{height}\" fill=\"white\"/><g font-family=\"sans-serif\" font-size=\"13\" fill=\"#222\"><text x=\"90\" y=\"25\">{System.Security.SecurityElement.Escape(title)}</text><path d=\"M90 50V440H950\" fill=\"none\" stroke=\"#666\"/>");
        for (var index = 0; index <= 4; index++)
        {
            var x = 90 + index * 860d / 4;
            var y = 440 - index * 390d / 4;
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:F1}\" y=\"460\">{maxX * index / 4:0}</text><text x=\"5\" y=\"{y:F1}\">{maxY * index / 4:0.##}</text>");
        }
        svg.Append($"<text x=\"450\" y=\"480\">{System.Security.SecurityElement.Escape(xLabel)}</text>");
        for (var index = 0; index < groups.Length; index++)
        {
            var color = $"hsl({(index * 137.508 % 360).ToString("0.##", CultureInfo.InvariantCulture)},65%,38%)";
            var ordered = groups[index].OrderBy(point => point.X).ToArray();
            svg.Append($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1\" points=\"");
            foreach (var point in ordered)
                svg.Append(CultureInfo.InvariantCulture, $"{90 + 860 * point.X / maxX:F2},{440 - 390 * point.Y / maxY:F2} ");
            svg.Append("\"/>");
            foreach (var point in ordered)
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{90 + 860 * point.X / maxX:F2}\" cy=\"{440 - 390 * point.Y / maxY:F2}\" r=\"3\" fill=\"{color}\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"90\" y=\"{510 + index * 17}\" fill=\"{color}\">{System.Security.SecurityElement.Escape(groups[index].Key)}</text>");
        }
        return svg.Append("</g></svg>").ToString();
    }

    private sealed record SeriesFit(string Series, JsonElement Row, string Step, string Predictor, Fit Model);
    private sealed record Prediction(double Value, double Low, double High);
    private sealed record Fit(int N, int Distinct, double Intercept, double Slope, double RSquared, double MeanX, double Sxx, double ResidualVariance)
    {
        public static Fit? Create((double X, double Y)[] points)
        {
            var distinct = points.Select(point => point.X).Distinct().Count();
            if (points.Length < 3 || distinct < 3) return null;
            var meanX = points.Average(point => point.X);
            var meanY = points.Average(point => point.Y);
            var sxx = points.Sum(point => (point.X - meanX) * (point.X - meanX));
            var syy = points.Sum(point => (point.Y - meanY) * (point.Y - meanY));
            var slope = points.Sum(point => (point.X - meanX) * (point.Y - meanY)) / sxx;
            var intercept = meanY - slope * meanX;
            var residual = points.Sum(point => Math.Pow(point.Y - intercept - slope * point.X, 2));
            return new(points.Length, distinct, intercept, slope, syy == 0 ? 1 : Math.Clamp(1 - residual / syy, 0, 1), meanX, sxx, residual / (points.Length - 2));
        }
        public Prediction Predict(double x)
        {
            var value = Intercept + Slope * x;
            var margin = T95(N - 2) * Math.Sqrt(ResidualVariance * (1 + 1d / N + Math.Pow(x - MeanX, 2) / Sxx));
            return new(value, Math.Max(0, value - margin), Math.Max(0, value + margin));
        }
        private static double T95(int degrees)
        {
            double[] table = [12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
                2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042];
            if (degrees <= table.Length) return table[degrees - 1];
            const double z = 1.95996398454;
            return z + (z * z * z + z) / (4 * degrees) + (5 * Math.Pow(z, 5) + 16 * z * z * z + 3 * z) / (96 * degrees * degrees);
        }
    }
}
