# 02: Harness design

## 1. Project

| Item | Decision |
|------|----------|
| Path | `src/perf/Gallery.Stress/Gallery.Stress.csproj` |
| Type | Console `Exe`, `net10.0`, `Nullable` and `ImplicitUsings` on. This matches `Gallery.ProcessMediator.csproj`. |
| References | `Gallery.Core` (project reference). It needs no reference to the xUnit test projects. |
| Solution | Add to `src/Gallery.slnx` so it builds with everything else. It has no tests, so `dotnet test` ignores it. |
| Internals | Use the **public API** for build and measurement. Only Phase 6 (catalog simulation) needs internals. Add `<InternalsVisibleTo Include="Gallery.Stress" />` to `Gallery.Core.csproj` only in that phase. |
| ImageSharp | Already a transitive dependency through `Gallery.Core`, and it uses the same `sixlabors.lic` mechanism. |
| Why not xUnit | Runs take hours, need to resume, need per-run child processes, and need CLI options. A small smoke test in `Gallery.Tests` covers correctness instead (see doc 04, Phase 2). |

Suggested source layout:

```
src/perf/Gallery.Stress/
  Program.cs                      // command dispatch only
  Cli/StressOptions.cs            // parsing and validation (counts like "100k", sizes like "1MB")
  Generation/SyntheticPngWriter.cs
  Generation/ComfyMetadataFaker.cs
  Generation/SizeDistribution.cs
  Build/BuildCommand.cs           // generate, ingest, checkpoint loop
  Build/BuildState.cs             // resumable state (JSON)
  Open/OpenOnceCommand.cs         // child-process body: one measured open
  Open/MeasureOpenCommand.cs      // parent: spawns open-once N times
  Open/ColdCache.cs               // standby-list purge (Windows, elevated)
  Open/VaultOpenListener.cs       // ActivityListener and MeterListener for doc 03 telemetry
  Profile/ProfileCatalogCommand.cs
  Simulate/SimulateCatalogCommand.cs   // Phase 6
  LargeImages/LargeImageSuiteCommand.cs // section 7
  Results/ResultWriter.cs         // JSONL plus environment capture
  Results/ReportCommand.cs
  README.md                       // operator instructions
```

## 2. Commands

All commands take `--workspace <dir>`. This folder holds `state.json`, `results/*.jsonl`, the passphrase file and
logs. It must **not** be inside the repository.

### `profile-catalog --db <path-to-gallery.db>`

Read-only. It needs **no passphrase** and never runs migrations: it opens a raw `SqliteConnection` with
`Mode=ReadOnly`, not `GalleryStore`. It outputs privacy-safe distributions:

- row count, and the p50, p95 and max of `length(record)` and `length(thumbnail)`, plus their sums
- `page_size`, `page_count`, `freelist_count`, file size, and WAL size
- per-table and per-index byte totals through `dbstat` if available, or otherwise estimated from `length()` sums
- the number of rows with `fingerprint IS NULL`, and the count of `drive_operations` by state

Purpose: calibrate the generator so synthetic catalogs look like the user's real one (Phase 0). Run it against the user's
real 28k gallery first.

### `build`

```
build --workspace D:\stress\ws --vault D:\stress\vault [--media-directory E:\stress\media]
      --staging D:\stress\staging --targets 100k,200k,400k --batch-files 10000
      --size-dist fixed:1MB --seed 42 --workers 4 --catalog-batch 16
      [--metadata-dist uniform:8KB-60KB] [--measure-open-at-targets --open-runs 3 --cache cold]
```

Targets are **media item counts** (user decision). The count suite exists to stress the catalog and unlock, and their cost
scales with item count, not media bytes. Small (~1 MB) files reach high counts with little disk. Large-file cost is measured
separately by the large-image suite (section 7).

Loop until the last target is reached:

1. **Precheck the disk**: free space on the staging, vault and media drives must be at least `batch bytes × 1.1 + 2 GB` beyond
   what the next step needs. If not, stop with a clear message. Never fill a disk.
2. **Generate** one batch of PNGs into `staging\batch-<n>\` (doc section 3). Record generation time and bytes.
3. **Ingest** with `ImportFolderAsync(staging\batch-<n>, Descendants, sourceRetention: DeleteAfterVerification,
   reportTimings: ..., options: new IngestionOptions { Workers, CatalogBatchSize })`. Write the
   `IngestionTimingReport` as one JSONL line to `results/ingestion.jsonl`.
   - Because sources are deleted after verification, the peak disk use is the vault plus one batch, not twice the vault.
4. **Update `state.json`** atomically (write to a temp file, then move): batch index, next seed index, cumulative files and bytes,
   catalog bytes. A killed run resumes from the last committed batch. Any partial staging batch is regenerated
   deterministically from its seed.
5. **At each target** (100k, 200k, 400k items), when `--measure-open-at-targets` is set, run `measure-open` (below)
   before continuing. Results are tagged with the target label.

Vault setup on first run: `GalleryService.Create`, then `SelectNewVaultAsync`, then
`CreateVaultAsync(passphrase, passphrase, "stress-<seed>", new MediaStorageOptions { MediaDirectory })`.
Generate a random passphrase and save it to `workspace\passphrase.txt`. This is test data only; never commit it.
Record the recovery phrase in the same folder.

Decision: **one vault grows through all targets** (100k, then 200k, then 400k). This avoids ingesting 700k items in total and
mirrors how real galleries grow. See doc 06, Q3.

### `measure-open`

```
measure-open --workspace ... --vault ... --runs 5 --cache cold|warm|both [--label 100k]
             [--page-size 49] [--wait-for-drive-idle]
```

The parent process spawns `Gallery.Stress open-once ...` **as a new child process for every run**. That way each run
sees a cold CLR, JIT and SQLite state, the same as a fresh app start. Before each cold run it purges the OS file cache
(section 5). The parent collects one JSON object per run from the child's stdout. It writes it to `results/open.jsonl`
and prints the median, min and max.

### `open-once` (internal; started by `measure-open`)

It runs the exact UI sequence from doc 01, A1. It times each step with `Stopwatch` and also listens to the doc 03
telemetry:

| Step | Call |
|------|------|
| `create` | `GalleryService.Create(vaultRoot, new GalleryRuntimeOptions { StateDirectory = workspace\state }, new HttpClient())` |
| `open` | `OpenExistingVaultAsync(vaultRoot)` |
| `unlock` | `UnlockWithPassphraseAsync(passphrase)` |
| `preferences` | `LoadPreferencesAsync()`, then `ConfigureAutoLock(prefs...)` |
| `load` | `LoadAsync()`, then build a `Dictionary<string, ImageRecord>` the same way the UI does |
| `media_options` | `LoadMediaStorageOptionsAsync()` |
| `ingestion_options` | `LoadIngestionOptionsAsync()` |
| `vision_settings` | `SettingsAsync()` |
| `storage_overview` | `CachedStorageOverviewAsync()` |
| `first_page_thumbnails` | `ThumbnailAsync(id)` for the first `--page-size` records. Use the UI's default sort; if not reproducible, use catalog order and note it. |
| `total_to_interactive` | sum of all the steps above |
| `drive_idle` (optional) | time until background drive work drains (`WaitForDriveOperationsAsync` if public; otherwise poll status) |

It also captures per run:

- process I/O counters before and after: `GetProcessIoCounters` on Windows (`ReadTransferCount`, `ReadOperationCount`)
- `GC.GetTotalAllocatedBytes(true)`, gen 0/1/2 collection counts, peak working set, and peak private bytes
- record count, catalog file size, and WAL size

**Drift guard.** A comment in `OpenOnceCommand` cites `Home.razor` `UnlockAsync` and `ReloadAsync`. Doc 04 Phase 7 proposes
moving that sequence into `Gallery.Core`, so the UI and the harness share one implementation.

### `simulate-catalog` (Phase 6; see doc 05)

### `report --workspace ...`

Reads `results/*.jsonl` and writes `results/report.md` and `results/*.csv`:

- ingestion throughput per batch: files/s, MiB/s, and stage p95 by stage, plotted against cumulative size
- open time by step against record count and against catalog bytes, cold and warm
- a fitted model per step: least squares on `t = a + b·N`, plus `t = a + b·bytes`, with R². Extrapolated to 1M and 2M records,
  and **clearly labelled as an extrapolation**.

## 3. Synthetic PNG generator

Requirements for the **count suite**: about 1 MB per file (`--size-dist fixed:1MB`, accepted within ±15%). The content must be
unique (ingestion de-duplicates by content hash) and deterministic (resumable). Images should behave like real ones for
decode, thumbnail and metadata costs. The **large-image suite** (section 7) reuses this generator with other sizes.

### Pixel content

For each file `i`, use `rng = new Random(HashCombine(seed, i))`. Do not use `System.Random` shared across threads.

1. **Target size**: draw `S` from `--size-dist`. The count-suite default is `fixed:1MB`. Also supported: `uniform:a-b` and
   `tier:<bytes>@<W>x<H>`, used by the large-image suite.
2. **Dimensions**: for the count suite, use SD-like sizes (1024×1024, 832×1216, 1216×832, 768×1344 and 1344×768). These
   keep the decode and thumbnail work per item realistic, and the noise amplitude, not the dimensions, sets the file size.
   A 1024² RGB image is 3 MB raw, so 1 MB needs moderate noise and is easy to hit. For `tier:` sizes, the dimensions are fixed by the tier.
3. **Fill**:
   - First, a seeded low-frequency field (a few gradient blobs). This gives thumbnails realistic structure, so they compress like
     real thumbnails rather than flat grey.
   - Then add per-pixel noise of amplitude `A`. Noise makes the full-resolution image hard to compress, which is what drives
     the file to the size target.
4. **Encode**: ImageSharp `PngEncoder { CompressionLevel = Level1, ColorType = Rgb, BitDepth = Bit8 }`.
   Check the size; if it is outside ±10% of `S`, adjust `A` (or the dimensions) and re-encode at most 2 times. Keep the result.
5. **Write** with `FileOptions.SequentialScan` / `WriteThrough=false` to `staging\batch-n\<aa>\<i>.png`. Use about 1,000
   files per directory to mirror real export folders.

Parallelism: one `Parallel.ForEachAsync` over the file indices of a batch. The degree is `--gen-threads`, by default
`ProcessorCount - workers`, so that you can overlap generating the next batch with ingesting the current one. Overlapping is optional and off
by default. Turn it off when measuring ingestion, so CPU contention does not distort the ingestion timings.

### ComfyUI metadata (important for catalog realism)

Doc 01, A4: `Metadata` is the raw embedded text and lives inside every encrypted record. Record size probably
drives unlock cost (H1). So every synthetic PNG gets:

- a `tEXt` (or `iTXt`) chunk `prompt` holding ComfyUI **API-format** JSON: a node graph with `KSampler`, `CheckpointLoaderSimple`,
  `CLIPTextEncode` (positive and negative), `EmptyLatentImage`, `VAEDecode` and `SaveImage`. Use seeded random prompt text,
  cfg, steps, sampler, scheduler and model names, so that `ComfyMetadata.Extract` produces a real `GenerationInfo`.
- a `workflow` chunk holding UI-format JSON, padded with seeded extra nodes up to a size drawn from `--metadata-dist`
  (default uniform 8–60 KB).

**Calibrate with Phase 0**: set `--metadata-dist` so the synthetic `length(record)` p50 and p95 match `profile-catalog` on
the real gallery.

### Throughput expectation

Not measured; treat as a placeholder until the Phase 2 smoke run. Noise generation plus Level1 deflate should run at roughly
50–150 MB/s per core. At 400k × 1 MB (≈400 GB), generation alone is likely under an hour on 8 cores. Ingestion is likely the slower
side, because it is per-item (read, SHA-256, full decode, thumbnail, AES-GCM, catalog write, verify, delete source). Plan for **a full
0→400k build taking many hours, possibly most of a day**, and design for resuming.

## 4. Result records (JSONL; privacy-safe)

All result rows share an `env` block captured once per process:

```json
{ "schemaVersion": 1, "runId": "…", "utc": "…", "gitCommit": "…", "harnessVersion": "…",
  "os": "…", "dotnet": "…", "cpus": 16, "ramBytes": …,
  "vaultDrive": "Fixed (NTFS)", "mediaDrive": "Fixed (NTFS)", "stagingDrive": "Fixed (NTFS)",
  "defenderExclusion": "user-declared:yes|no|unknown", "power": "AC|battery|unknown" }
```

Drive kinds reuse the same idea as `IngestionTimingReports.DescribeDrive`. That method is internal; copy its logic. Do not record
drive letters or paths.

- `ingestion.jsonl`: `{ env, label, batch, cumulativeFiles, cumulativeMediaBytes, catalogBytes, report: <IngestionTimingReport fields>, generationMs, generatedBytes }`
- `open.jsonl`: `{ env, label, run, cache: "cold|warm", cachePurge: "standby-list|none|failed", records, catalogBytes, walBytes, steps: { create, open, unlock, … }, telemetry: { <doc 03 tags> }, io: { readBytes, readOps }, gc: { allocatedBytes, gen0, gen1, gen2 }, peakWorkingSet }`
- `catalog-profile.jsonl`: output of `profile-catalog`

Never record file names, folder names, ids, prompts or paths. The `workspace` and `vault` paths are inputs and are never echoed into results.

## 5. Cold-cache control (Windows)

"Fresh uncached" means `gallery.db` and the media directories are not in the OS standby list. Options, in order of preference:

1. **Purge the standby list** (default for `--cache cold`). This needs an elevated process. Enable
   `SeProfileSingleProcessPrivilege`, then call `NtSetSystemInformation(SystemMemoryListInformation = 80,
   MemoryPurgeStandbyList = 4)`. Record `cachePurge: "standby-list"`. If the process is not elevated, record `cachePurge: "failed"`
   and **warn loudly**; do not silently run warm.
2. **Dismount and remount a dedicated VHDX** holding the vault (`Dismount-VHD`, then `Mount-VHD`; also elevated). This invalidates the cache
   for that volume only. Document it in the harness README as the alternative when (1) is unavailable.
3. **Reboot**. This is the manual fallback. Run `open-once` directly after logon, with `--cache cold --cache-purge none-declared-reboot`.

Warm runs: run `open-once` once untimed, then N timed runs.

Also record whether the vault drive is an SSD or an HDD, if detectable (`MSFT_PhysicalDisk.MediaType` through WMI, best effort).
Cold-cache cost depends heavily on it.

## 6. Safety rails

- Refuse to run if `--vault`, `--staging`, `--media-directory` or `--workspace` is inside the git repository or
  `%LOCALAPPDATA%\DiffusionGenGallery`.
- Refuse to use a vault directory that is not marked as a stress vault, through a `stress-vault.marker` file written at creation.
  This prevents accidentally ingesting into or purging a real gallery.
- Use its own `StateDirectory` (`workspace\state`), so the app's `VaultHistory` (the last-opened gallery) is never changed.
- `Ctrl+C` stops cleanly after the current file. The state file stays consistent.
- `cleanup --workspace ...` deletes staging and, only with `--delete-vault` and the marker present, the vault.

## 7. Large-image suite (`large-images`)

Purpose: measure the per-file cost that the 1 MB count suite deliberately leaves out. That covers decrypting large media,
full decode, thumbnail generation, ingestion memory per worker, and viewer load. It is small and fast, and it runs in its **own
fresh stress vault**, so its timings are not mixed with the catalog-size results.

### Facts that bound the tiers (from the code)

- `GalleryService.MaxFileBytes = 50 * 1024 * 1024` (`GalleryService.cs:114`). Larger images are rejected.
- `ImportMediaValidator`: **at most 40,000,000 pixels**, and at most 16,384 px per side (`ImportMediaValidator.cs:8–9, 27–28`).
- Thumbnails decode the full image (`Image.LoadAsync<Rgba32>`, `GalleryService.cs` ~582) and resize it to 384 px or less.
- Viewer paths decrypt the whole file: `PreviewAsync` and `ViewerImageAsync(id, Fit)` decode and resize to 1600 px.
  `ViewerImageAsync(id, Original)` base64-encodes the whole plaintext, so a 48 MB file becomes a string of about 64 MB.

### Tiers (default)

The cost of decode and thumbnail generation follows **pixel count**. The cost of read, hash, encrypt and decrypt follows **bytes**.
Both therefore rise together, up to the import ceiling:

| Tier | File size | Dimensions | Pixels | Represents |
|------|-----------|------------|--------|------------|
| L1 | 4 MB | 2048×2048 | 4.2 M | 2× upscale of SDXL |
| L2 | 8 MB | 2944×2944 | 8.7 M | |
| L3 | 16 MB | 4096×4096 | 16.8 M | 4× upscale of SDXL |
| L4 | 32 MB | 5120×5120 | 26.2 M | |
| L5 | 48 MB | 6272×6272 | 39.3 M | **at the import ceiling** (just under 40 M px and 50 MB) |

**50 images per tier** (250 files, about 5.4 GB). That gives a stable p50, and a usable p95 (around the 3rd-slowest
sample). The whole suite should take minutes, not hours.

Optional **decoupling pair** (`--decouple`): 50 × 4096² at 8 MB, and 50 × 4096² at 32 MB. Same pixels, 4× the bytes. This shows
directly whether a stage is driven by bytes or by pixels.

**Rejection edge cases** (5 files each; a correctness check, not timed): 50 MB + 1 byte; 6400×6400 (41 M px); 16,448×512.
Expected for each: rejected with a clear error, no catalog row, no media file, and the source file kept.

### Steps per tier

1. Generate the tier with the section 3 generator (`--size-dist tier:<bytes>@<W>x<H>`, a seed per tier).
2. Ingest twice, each time into a fresh vault (or a fresh tier subfolder in it):
   - with `--workers 1`, which gives clean per-file latency
   - with `--workers 4` (the default), which shows memory pressure. Decoding one L5 image as `Rgba32` takes about 157 MB, before
     resize buffers, so 4 workers may peak above 1 GB.
   Record the `IngestionTimingReport` stages **Decode, Thumbnail, Encrypt, WriteMedia and Verify** (p50 and p95), plus peak working set,
   peak private bytes, LOH size and the gen-2 GC count.
3. **Viewer timings** for the first 20 items of each tier, cold and warm, each in a fresh child process (the same pattern as `open-once`):
   `PreviewAsync(id, Fit)`, `ViewerImageAsync(id, Fit)`, `ViewerImageAsync(id, Original)` and `VerifyImportedAsync(id)` (decrypt plus verify).
4. Write `results/large-image.jsonl`: `{ env, tier, workers, bytes, width, height, stage: {p50, p95, max}, viewer: {...}, memory: {...} }`.

### Acceptance

- All 250 tier files are accepted. Every rejection edge case is rejected as expected.
- `report` shows a table per tier, plus a fit of stage time against bytes and against pixels.
- Flag any tier where peak memory with `--workers 4` exceeds 50% of RAM, or where `ViewerImageAsync(Original)` takes more than 2 s warm.
  These thresholds are adjustable.
