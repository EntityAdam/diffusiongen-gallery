# Gallery stress and performance harness

`Gallery.Stress` is a separate, opt-in .NET 10 console program. It generates disposable synthetic PNGs, ingests through the production pipeline, and measures the Core work from opening a vault through first-page thumbnails. Normal application startup and `dotnet test` do not launch stress runs. Browser rendering and JavaScript interop are excluded. Measurements use the production first-page browsing API.

Build from the repository root:

```sh
dotnet build src/Gallery.slnx --nologo -v q
dotnet run --project src/perf/Gallery.Stress --no-build -- --help
```

For useful performance measurements, build with `-c Release` and use `dotnet run -c Release --no-build`. Commands below use the shorter direct DLL invocation after a Release build:

```sh
dotnet build src/Gallery.slnx -c Release --nologo -v q
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll --help
```

Every command takes `--workspace`. Workspace, vault, staging and external media directories must be outside the repository and the application's `DiffusionGenGallery` data directory. Workspace, staging and vault must be separate, disjoint directories. Symbolic links are resolved before validation. Use dedicated empty directories. The harness keeps its own application history under `workspace/app-state`.

The workspace contains `passphrase.txt`, `recovery.txt`, checkpoints, ownership manifests and `results/*.jsonl`. Credentials use production KDF settings and are disposable; do not reuse them or commit them. Unix credentials are owner-readable/writable. Result rows contain environment details, counts, sizes and durations; they exclude paths, media IDs, prompts and credentials. State/manifests necessarily contain local paths and are not shareable measurement results. Keep the workspace together with its vault when resuming.

## Profile and calibrate

Start with a read-only profile of the real catalog. This command uses a raw SQLite `Mode=ReadOnly` connection, requires no passphrase and never runs migrations:

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll profile-catalog \
  --workspace /outside/stress/profile --db /real/gallery/gallery.db
```

It records record/thumbnail encrypted-length distributions, SQLite page counts, WAL size, missing fingerprints, queue-state counts and `dbstat` object sizes (or a clearly labelled payload-size estimate). Adjust `--metadata-dist` until synthetic record p50/p95 resemble the profile. A quantile profile cannot reproduce the complete distribution: `simulate-catalog --record-dist .../catalog-profile.jsonl` uses an explicitly labelled approximation, not an exact sample distribution.

## Build a growing gallery

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll build \
  --workspace /outside/stress/count-workspace --vault /outside/stress/count-vault \
  --staging /outside/stress/count-staging --targets 100k,200k,400k \
  --batch-files 10000 --size-dist fixed:1MB --metadata-dist uniform:8KB-60KB \
  --seed 42 --workers 4 --catalog-batch 16 --gen-threads 4 \
  --measure-open-at-targets --open-runs 3 --cache both
```

Windows paths work too, for example `D:\stress\workspace`; use your shell's continuation syntax. `k`/`M` counts are decimal (100k = 100,000), while KB/MB/GB and KiB/MiB/GiB are powers of 1024. `fixed:`, `uniform:min-max`, and `tier:bytes@WIDTHxHEIGHT` size distributions are supported. The generator uses seeded per-file randomness, an SD-sized colour field/noise pattern and valid ComfyUI API/workflow PNG text chunks. Generation and ingestion run sequentially so their CPU costs are separate.

One vault grows through every target. Sources are deleted only by production ingestion after verification. Finder's `.DS_Store` files are ignored during staging initialization and batch-completion checks; other unexpected files still block completion. A batch's seed range is saved before work begins; the completed checkpoint is replaced atomically. After an interruption, committed items are matched by original source path and their stored media is verified; deleted committed sources are not regenerated. Existing committed sources must match the catalog SHA-256 before deletion. Uncommitted staging files with workspace generation receipts are reused only after their length and SHA-256 match. Missing, changed, or unreceipted uncommitted files regenerate deterministically. Older interrupted builds have no receipts, so their uncommitted sources regenerate once. Receipts are bound to the saved seed range and distributions and removed after the batch checkpoint advances. Retried result rows have a stable batch number and are deduplicated by `report`. A recovered fully committed batch has no new ingestion timing report. Do not edit the vault, remove its records, change its media setting, or run another mutating harness command while a build is in progress.

Count-only phase progress prints every five seconds during opening, generation, recovery, ingestion and target measurements; results and `state.json` still describe completed batches. `Pending` retains the original batch size on resume even when `--batch-files` changes. Ingestion result rows are saved after each completed batch. Open measurements run at the configured targets (100k, 200k and 400k by default). If the first batch fails before saving its result, `report` has no timing data to display even when records have already been imported; rerun the build to reconcile the pending batch and continue.

Ctrl+C cancels generation or stops ingestion through its progress callback, after active production work drains. Resume with the same workspace, paths, seed and distributions; target counts can be extended. A hard kill can leave uncommitted ciphertext orphan files, just as an interrupted production import can; counts and fingerprints still reconcile on resume.

For a local acceptance smoke run, replace targets with `1k,2k` and `--batch-files 500`. Check four ingestion rows, 2,000 catalog rows, no remaining PNG sources, distinct open PIDs and matching `gallery.load.record_count`. To exercise recovery, terminate during a batch and rerun the identical command.

### Measure a new Turso gallery

Add `--catalog-backend turso` when creating a gallery. SQLite remains the default
for new galleries. This creates and measures a disposable Turso gallery through
the normal generation/import pipeline:

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll build \
  --workspace /outside/stress/turso-workspace --vault /outside/stress/turso-vault \
  --staging /outside/stress/turso-staging --catalog-backend turso \
  --targets 1k,2k --batch-files 500 --size-dist fixed:1MB \
  --metadata-dist uniform:8KB-60KB --seed 42 --workers 4 --catalog-batch 16 \
  --measure-open-at-targets --open-runs 3 --cache warm --page-size 49
```

Use separate workspace/vault/staging directories and `--catalog-backend sqlite`
for a comparison. Align seed, distributions, counts, workers, page size, GC/cache
conditions, and optional waits. `simulate-catalog` and `large-images` also accept
the option; large-image children inherit it.

Existing galleries are detected from their vault header when the option is
omitted. An explicit backend must match that header, and checkpoints retain the
backend so a resume cannot silently switch formats. Old checkpoints without a
backend retain their SQLite meaning. This option never converts a gallery.

Later measurements detect Turso automatically:

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll measure-open \
  --workspace /outside/stress/turso-workspace --vault /outside/stress/turso-vault \
  --runs 3 --cache warm --page-size 49 --wait-for-catalog --label turso-2k
```

Result rows record `env.catalogBackend`; reports, CSV series, and plots keep
`sqlite`, `turso`, and older unrecorded backends separate. Turso selects the first
page with an SQL index, so `firstPageCacheHit: false` does not imply a full scan.
`--wait-for-catalog` measures the existing full-record background load.
SQLite record-decryption/JSON byte and phase telemetry is unavailable for Turso;
its `recordBytes` remains null and the CSV field is empty. Use shared opening
milestones, process I/O, memory, and catalog/WAL sizes for comparisons. Reports
use database-plus-WAL bytes for catalog-storage plots/models; raw database and
WAL sizes remain separate in JSON/CSV, since Turso can retain changes in the WAL.

Turso reuses one serialized native connection throughout an explicit leased import/recovery operation. The connection closes before its vault-key lease is released, so vault close and relocation drain it normally. Settings and isolated reads outside such operations use transient connections. FULL durability and encrypted-media read-back verification remain enabled.

The adapter requests a truncating WAL checkpoint at completed-operation boundaries when the WAL reaches 64 MiB, including when opening an older large WAL. One transaction can exceed that threshold; a large simulated-catalog commit can therefore produce a larger temporary WAL. Automatic maintenance makes one attempt and, if it fails, records a failure and waits at least 30 seconds before retrying on a later operation. It cannot turn a successful catalog commit into an import failure. Snapshot/publication checkpoints are mandatory and still fail the operation if unsuccessful. An older multi-gigabyte WAL can make the first startup/checkpoint slow; the heartbeat continues during that work.

Build rows include `catalogMetrics`: native connection opens/duration, serialized gate wait, observed WAL bytes, checkpoint count/duration and automatic checkpoint failures. `regeneratedFiles` counts newly generated or regenerated sources, `reusedFiles` counts receipt-validated files, and `committedFiles` counts sources already catalogued on recovery. These counters do not replace completed-batch checkpoints. The row includes `generationMs` and `recoveryMs`; ingestion `report.wallMs` includes maintenance performed during that import. Compare those phases and checkpoint costs separately; opening an old WAL before the batch starts is outside its metrics.

Turso remains experimental. `profile-catalog` still supports only the legacy
SQLite format and its encrypted-blob distributions; it does not unlock a Turso
database. The synthetic `--record-dist` approximation is calibrated to those
legacy blob lengths, so do not claim matching Turso record sizes from it.

## Open measurement

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll measure-open \
  --workspace /outside/stress/count-workspace --vault /outside/stress/count-vault \
  --runs 3 --cache both --page-size 49 --wait-for-drive-idle --label 400k
```

Each run is a fresh child process, including the untimed warm-up before the warm series. The web app and harness import `src/Gallery.Runtime.props`, which enables server GC for both. Each result records the **effective** `env.gcMode` and `env.gcLatencyMode`, including runtime overrides; configuration alone is not proof of the effective mode.

Steps mirror `Home.razor` `UnlockAsync` / `OpenGalleryAsync`: service creation, open, unlock, preferences/auto-lock, initial page, storage/ingestion options, dictionary, vision settings, cached overview, first-page selection, and thumbnails. Unless `--page-size` is supplied, the saved density selects 12 items (detail) or 49 (compact), matching the UI. The saved trash tool selects marked records. Use `--page-size 49` to retain the older harness's thumbnail count.

Schema 3's primary metric is `steps.passphrase_to_gallery_ready`: time from passphrase submission until first-page records are selected and settings are available, **before** fetching thumbnails. For SQLite, a valid encrypted browse index bounds this work to page records. Missing or invalidated SQLite indexes require a full snapshot to rebuild; an untimed warm-up can pay that one-time cost. Turso uses indexed SQL instead. Inspect `env.catalogBackend`, `firstPageCacheHit`, `loadedRecords`, `records` (full catalog count), and `catalogCompleteAtOpen` to distinguish these paths. Schema 2 measured full-catalog readiness before thumbnails; reports keep it separate from schema 3 and the older startup-through-thumbnail target.

`milestones` records `key_unlocked`, `gallery_ready`, and `thumbnails_ready`, relative to passphrase submission. `catalog_loaded` appears only when the full catalog is loaded. `first_page_selection` separates filtering/sorting from `first_page_thumbnails`. The legacy `total_to_interactive` field still includes startup and thumbnails. Browser paint, grouping and user interaction are not simulated; Core readiness is not a browser-render measurement.

By default the harness stops after thumbnails; the UI starts a cancellable full-catalog load after displaying the initial page. Add `--wait-for-catalog` to measure that scan separately as `steps.catalog_background` and `milestones.catalog_loaded`. It runs after thumbnails, without changing the primary opening timer. `--wait-for-drive-idle` waits for scheduled file work after that. Resource totals include optional waits, and reports separate wait scopes so background memory is not mixed with first-page-only measurements. A cached opening's small memory footprint does not represent eventual full-library memory use.

Rows include activity tags, catalog meter distributions, process PID, allocations/GC counts, GC pause milliseconds, managed heap estimates, peak resident memory/private commit, catalog/WAL sizes and process read counters. Windows uses `GetProcessIoCounters`; Linux uses `/proc/self/io`; macOS uses `proc_pid_rusage` for disk-read bytes and `getrusage` for peak resident bytes. macOS read-operation counts and peak private bytes are unsupported and remain `null`. Read-byte counters have platform-specific semantics; compare within the same OS. Reports show unavailable values as **—**, preserving supported zero I/O counts. Peak memory is the process lifetime high-water mark, including startup. GC pause time overlaps phase timings. Managed bytes at the end are an uncollected heap estimate, not proven live bytes; last-GC heap size, fragmentation and committed bytes are separate snapshots. Resource measurements cover the whole measured operation including thumbnails and optional drive-idle wait.

Catalog telemetry retains `gallery.load.query_read_ms` as the legacy **residual** outside decrypt and deserialize. New `query_step_ms`, `blob_materialize_ms`, and `other_loop_ms` split out query execution/stepping, encrypted-blob materialization, and remaining loop work. Native database reads and GC can occur inside those phases; none is a pure disk-I/O timer. `gallery.load.gc_pause_ms` measures process GC pauses during the scan and overlaps those timings. Runtime memory budget is recorded as `ramBytes`, and may differ from physical RAM. Unknown power state, disk medium and undeclared antivirus status are reported as unknown. Use `--defender-exclusion yes|no|unknown` to record your exclusion declaration. Only drive kinds/filesystem types, never drive names, are recorded.

### Cold cache

Windows cold runs require an elevated shell. The harness enables `SeProfileSingleProcessPrivilege` and purges the system standby list using `NtSetSystemInformation`. This affects the machine's cache, so run measurements on an otherwise idle machine. A purge failure or non-Windows host emits a warning and records `cachePurge: "failed"`; reports keep these separate from verified cold runs. The child is still a fresh process. Warm runs prewarm the vault in an untimed child.

Alternative cold procedures are dismount/remount of a dedicated VHDX (`Dismount-VHD` / `Mount-VHD`, elevated PowerShell) or reboot. After reboot, run the internal body once and capture its JSON yourself:

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll open-once \
  --workspace /outside/stress/count-workspace --vault /outside/stress/count-vault \
  --cache cold --cache-purge none-declared-reboot
```

Declared reboot runs are kept distinct from automatically verified standby-list runs. Do not describe a failed purge as cold evidence.

### Measuring a real gallery

`measure-open` normally requires `stress-vault.marker`. Explicit `--allow-real-vault --passphrase-file <file>` permits measurement of an existing real gallery, with a separate external workspace. **This follows normal application opening, including production migrations, metadata backfill and background drive operations; it is not read-only.** Close the app first, retain a backup and use normal vault credentials from a local file. The marker is never added to a real vault, and build/cleanup do not accept this override. Begin with three cold and three warm runs on the real 28k-item gallery; these results are more informative than an uncalibrated synthetic catalog.

## Large images

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll large-images \
  --workspace /outside/stress/large-workspace --vault /outside/stress/large-vaults \
  --staging /outside/stress/large-staging --images-per-tier 50 --viewer-samples 20 --workers 4
```

Five tiers use 4/8/16/32/48 MiB and 2048²/2944²/4096²/5120²/6272² pixels. Each tier is ingested into fresh vaults using one worker and four workers; ingestion and each cache-mode viewer measurement run in fresh child processes. Cold viewer operations attempt to purge separately before preview, fit, original and verification. `--decouple` adds 4096² images at 8 and 32 MiB. `--tiers L1 --images-per-tier 1 --viewer-samples 1` is a small smoke run. All fifteen rejection checks (too many bytes, pixels or side length) run after selected tiers and retain their source files for inspection.

Results record ingestion stage p50/p95/max, viewer latency, process peaks, LOH and gen-2 collections. LOH is the last available GC snapshot, not a continuous high-water mark. Flags use defaults of 50% of runtime memory budget and 2,000 ms for warm original-image viewing, adjustable with `--memory-warning-percent` and `--original-warning-ms`. This suite creates a new set of child vaults on each invocation; it does not resume an interrupted suite.

## Simulate a larger catalog

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll simulate-catalog \
  --workspace /outside/stress/sim-workspace --vault /outside/stress/sim-vault \
  --rows 500k,1M,2.1M --thumbnail-kb 40-120 --seed 42 \
  --record-dist /outside/stress/profile/results/catalog-profile.jsonl \
  --measure-open-at-targets --open-runs 3 --cache both
```

The command creates a normal vault using the selected backend, then writes through its production catalog adapter in 5,000-row transactions with unique source hashes. SQLite stores encrypted JSON records/thumbnails; Turso stores typed records and thumbnails under native database encryption. It resumes from committed row count and validates stored configuration. `--sparse-media` creates NTFS sparse placeholder files on Windows only; otherwise no full media files are generated. Placeholder thumbnails/media are for catalog measurements and cannot be opened as real images. Do not use this vault for viewer testing. All results carry `simulated: true`; report series remain separate from real ingestion. `--rows 10k --metadata-dist fixed:8KB --thumbnail-kb 1-2` is a small simulation smoke run.

At nearby sizes compare real and simulated slopes. A disagreement over 15% means the simulated shape needs calibration. Extrapolation cannot predict memory exhaustion or a catalog that outgrows RAM, and sparse enumeration does not reproduce media reads. If a tier will not fit, stop at the previous tier and report its failure/limit.

## Reports and cleanup

```sh
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll report --workspace /outside/stress/count-workspace
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll cleanup --workspace /outside/stress/count-workspace
# Also delete marked synthetic vaults:
dotnet src/perf/Gallery.Stress/bin/Release/net10.0/Gallery.Stress.dll cleanup --workspace /outside/stress/count-workspace --delete-vault
```

Reports write Markdown, CSV and SVG plots under `results`. Catalog backends, schema versions, effective GC modes, GC latency modes, page sizes, environments, cache-purge status and simulation remain separate series. Old results without recorded GC mode remain **unknown**; they are never retroactively labelled workstation GC. Hypothesis evidence is evaluated separately for each compatible series. Reports show H1–H6 evidence conservatively, and fit time against records/record bytes/catalog bytes with R² and 95% prediction intervals. Fits require three distinct sizes. Predictions at 1M, 2M and 2.1M records are explicitly extrapolations; they assume a stable record shape and linear behavior. Review allocation/peak-memory growth before trusting them. Incomplete JSONL lines are skipped with a warning.

Cleanup uses ownership manifests and markers. It removes registered staging; `--delete-vault` also removes marked vaults, including simulation and nested large-image vaults. It keeps the workspace/results/credentials. External media is retained: review and remove its dedicated directory manually after deleting the associated vault. Parent containers and empty suite folders can remain. A cleaned build needs fresh directories/workspace before rebuilding.

## Budget and measurement conditions

| Items | Synthetic media | Catalog estimate | Approximate vault-drive free space |
|---:|---:|---:|---:|
| 100k | 100 GB | 18 GB | 130 GB |
| 200k | 200 GB | 36 GB | 250 GB |
| 400k | 400 GB | 72 GB | 490 GB |
| 2.1M simulated | none/full sparse logical size | 380 GB | calibrate first |

These are planning estimates from the reported real catalog, not measured output. Peak staging is one batch (about 10 GB at the default). Free-space checks aggregate requirements on each drive and reserve another 10% plus 2 GiB. Reduce batch size when space is limited. Large-image comparisons retain two encrypted copies per tier, so allow about 20 GB plus headroom for the default suite, more with the decoupling pair. Large writes consume SSD endurance. Keep AC power and power settings consistent. Consider a dedicated antivirus/Defender exclusion for disposable stress directories if appropriate, and record that choice with `--defender-exclusion yes`, `no`, or `unknown`; the harness never changes system exclusions.

For the real application, attach standard diagnostics to its PID:

```sh
dotnet-counters monitor --process-id <PID> --counters Gallery.Core.Catalog
dotnet-trace collect --process-id <PID> --providers 'Microsoft-Diagnostics-DiagnosticSource:0x3:5:FilterAndPayloadSpecs=[AS]Gallery.Core.VaultOpen'
```

Start collection before unlocking. `Gallery.Core.VaultOpen` activities contain unlock/load stage tags; `Gallery.Core.Catalog` exposes connection count, open/migration duration and thumbnail duration/bytes. No additional listener means no per-row timing instrumentation. Run tests with `dotnet test src/Gallery.slnx --no-build --nologo -v q`; on macOS, use `TMPDIR=/private/tmp` if the default temp location traverses `/var` and the application's vault-path validator rejects that symbolic link.
