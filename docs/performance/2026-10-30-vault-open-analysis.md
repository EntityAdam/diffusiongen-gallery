# Vault-open measurements and improvement options

Date: 2026-10-30. Analysis of the supplied count-suite `open.jsonl`, `ingestion.jsonl`, generated report and build checkpoint, followed by the implemented first-page opening and validation. The historical analysis below describes the implementation before this change.

## Implemented result

The application now opens from a bounded encrypted browse index stored in the existing settings table. Its catalog revision and page records are read in one SQLite snapshot. A cache miss performs a full scan and rebuild; successful subsequent opens batch-read only the first page's authoritative records. Ratings, favorites and notes preserve the index transactionally. Imports, removals and deletion-mark changes invalidate it through the existing revision triggers. The completed catalog load rebuilds it with a revision compare-and-save, so a concurrent mutation cannot publish stale membership.

The UI publishes real page records first, then starts one cancellable background catalog scan off the renderer thread. Search, filters, grouping, pagination, ingestion, duplicate finding and global counts remain pending until the full snapshot is current. Selection, preview and item edits work on the initial page. Lock, timeout, gallery switching and component disposal revoke/cancel the scan; generation checks prevent late publication. The eventual full-record memory cost remains.

### Measurements on the existing 350k vault

Three fresh-process warm runs per series, server GC, saved detail density (12 records). Original rows remain unchanged. New rows use label `350k-first-page`, schema 3; the aligned full-load baseline uses `350k-aligned`, schema 2. Both primary timers start at passphrase submission and exclude thumbnails; their readiness definitions differ intentionally.

| Measurement | Full-catalog open | Cached first-page open |
|---|---:|---:|
| Median passphrase to Core readiness | 99.705 s | 0.183 s |
| Range | 96.479–100.458 s | 0.181–0.198 s |
| Records materialized before readiness | 350,000 | 12 |
| Encrypted record bytes read | 13,488,924,186 | 392,883 |
| Median process peak resident memory | 28,730 MiB | 91.7 MiB |
| Median cumulative managed allocations | 51,233 MiB | 12.4 MiB |

This is approximately a **545× reduction in time to the selected readiness milestone**, after the encrypted index has been built. First-page-only harness memory includes thumbnails and excludes the UI's later background catalog load; it is not a claim that full-library memory has been reduced. Warm-up performed the one-time index build. These three warm samples do not establish a cold-cache result, a p95 target across sizes, or an extrapolation to larger libraries.

A headless Chromium check against the same 350k vault rendered 12 cards in **305 ms** from the unlock click. It verified the pending search/pagination state, opening a full item preview, and Panic Lock canceling background work without stale UI publication or browser errors. This is a browser smoke measurement, separate from the repeatable Core benchmark.

A second browser check displayed the initial page in **227 ms**, then completed the background catalog load in **154.4 s** from submission. Search controls re-enabled and page two worked; no browser errors occurred. That longer background time is a single UI run with other validation work active, not a controlled full-scan comparison. The slow full-library path remains a follow-up optimization.

The final build's browser smoke check rendered the initial page in **198 ms** and repeated preview/lock checks, also confirming that the detail viewer's global prompt filter stays disabled while the catalog is pending. Release build passed without warnings; .NET regression tests passed **811**, skipped **5**; JavaScript tests passed **25**. An isolated 1,030-row CLI smoke run verified 49-record opening and the optional background-catalog timer. The six originally supplied open rows retain SHA-256 `af7e44347bbb4aea4d5d210742f95eea230a8089049f5080a5f7627bdad6e130`.

Schema 3 records first-page cache hit, loaded count, total count and completeness. `--wait-for-catalog` optionally times the remaining scan after the opening milestone. The default harness does not run the UI's background scan; optional wait scopes are separated in reports. No database copy, schema migration or media rewrite was needed.

## Desired behavior

The user selected: **first page usable; full-library search and counts may finish in the background**. Thumbnail completion is not required for the gallery to count as open.

Measure passphrase submission to the first usable page separately from thumbnail completion, full-library readiness, and background-drive completion. Rendering the shell alone does not satisfy the goal: the first page must contain actual records with working selection/viewer actions.

## Earlier harness alignment

Implemented after this analysis: shared server-GC defaults for app and harness; schema 2 with effective GC mode/latency, passphrase-to-gallery-ready time excluding thumbnails, separate query-step/blob/other-loop timers, GC pauses and heap estimates, native macOS peak memory/read-byte counters, and report separation for legacy/new measurements. The numbers below remain the original unmodified baseline; the old rows' effective GC mode remains unknown. Small-vault smoke checks validate the instrumentation, not a new large-gallery performance result.

## Original measurements before alignment

The build reached **350,000 committed items**. Its checkpoint reports 369.09 GB of synthetic media and a 49.51 GB catalog, in decimal units. Open measurements were collected at **100,000 and 200,000 items**, three warm runs each. There is no measured open at 350,000 or 400,000. All recorded opens have `cache=warm`, `cachePurge=none`.

Ingestion throughput stayed near 33–35 files/s through most batches. Catalog growth and cumulative generation/ingestion work are approximately linear. **Measured open latency is not linear across the available sizes:** doubling the item count increased median total latency 4.11× and full-load latency 4.65×. Two sizes cannot establish an extrapolation model.

| Measurement | 100k items | 200k items |
|---|---:|---:|
| Total through thumbnails, median | 5.828 s | 23.938 s |
| Same run totals minus thumbnail step, median | 5.761 s | 23.829 s |
| Unlock, median | 0.073 s | 0.070 s |
| Full catalog load, median | 5.101 s | 23.707 s |
| Decrypt inside load, median | 1.043 s | 2.187 s |
| JSON deserialize inside load, median | 3.108 s | 6.572 s |
| Query/read residual inside load, median | 0.937 s | 14.925 s |
| Thumbnail step, median | 0.067 s | 0.110 s |
| Encrypted record bytes read | 3.585 GiB | 7.179 GiB |
| Total managed allocations per open, median | 14.376 GiB | 28.621 GiB |

Stage medians need not add exactly to the median total. The thumbnail step also includes the harness's call to the default filter/sort. Subtracting it is a useful Core-only approximation, not an actual browser measurement of first-page rendering.

Allocations are cumulative allocation traffic, **not peak resident memory**. Recorded peak working set is zero and process read counters are null on this Mac; neither is evidence of zero memory use or zero disk I/O.

## Original source evidence and interpretation

1. `Home.razor:1204` awaits `Gallery.LoadAsync()` before constructing the browsing projection and first page. `GalleryStore.cs:212` reads, decrypts, deserializes, and retains every full `ImageRecord`. Those records include raw workflow metadata and other long strings, although the first page needs only 12 or 49 items.
2. `Home.razor:1224–1295` already schedules thumbnail loading in the background, with a yield before work. Deferring thumbnails further cannot remove the 5–24 second catalog load. The harness currently waits for their completion, so it measures a different milestone from the requested first-usable-page milestone.
3. `GalleryStore.cs:235` calculates `query_read_ms` by subtracting decrypt and deserialize timers from the surrounding loop. It includes SQLite work, encrypted-blob allocation/copying, envelope validation, plaintext clearing, list growth, and any GC or scheduling delay outside those two timers. The 0.94 → 14.93 second jump must not be called proven disk-I/O latency.
4. Decrypt and deserialize scale roughly with item count, while the residual changes much more sharply. Memory/cache pressure and native database reads are candidates, not established causes. No generation or fingerprint backfills occurred, no new media-index scan was scheduled, and gallery-gate wait was negligible in these samples.
5. PBKDF2 itself was about 61–65 ms. Keep the production KDF and authenticated encryption. Some migration histograms contain 0.3–0.47 s outliers, while other runs total about 9 ms; connection/migration tuning cannot explain or eliminate the dominant 200k load.
6. The web app's Release runtime configuration enables `System.GC.Server`; the stress executable's does not. Standalone .NET applications default to workstation GC unless overridden. Effective GC mode was not recorded, so the old results cannot prove which mode ran. Match the app configuration and record `GCSettings.IsServerGC` before treating future comparisons as equivalent. GC tuning changes throughput and memory behavior; an improvement is not guaranteed. See [Microsoft GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector) and [workstation/server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc).

7. The current nonempty text-search predicate concatenates names, raw metadata, descriptions, and tags into a new string for each candidate record (`GalleryFilterProcessor.cs:13`). Once search is enabled, that can create another large allocation burst. Removing that concatenation requires preserving existing matching semantics, including matches across joined field boundaries. Fast opening alone does not solve search responsiveness.

## Options

### 1. Open the first page from a small encrypted index; finish the catalog in the background

**Recommended first implementation for the selected readiness definition.**

Persist an authenticated, encrypted index of the actual first-page IDs in default browse order. Read that small index after unlock and batch-read only those records from the authoritative catalog. At this dataset's mean record size, 49 full records represent roughly 1.9 MB of encrypted record data rather than 7.7 GB at 200k items. This is a data-volume comparison, not a measured speedup.

The existing encrypted settings table could store this derived index, avoiding a table rewrite for an initial implementation. The index must carry a format version and catalog revision and be maintained or invalidated atomically with imports, edits, removals, and restores. Capture the page IDs and revision from one consistent database snapshot. Fetch page records under an appropriate read snapshot as well. An invalid or missing index must trigger a safe rebuild; the first open after upgrade may still pay the current full-load cost. Never silently use stale IDs or show deleted records.

Use full `ImageRecord` objects for those page items so existing actions do not receive truncated records. Represent the UI's catalog completeness explicitly; page-local counts must never masquerade as library-wide counts. Load the remaining catalog under a bounded, cancellable background operation and publish global counts/search when ready. Background work must not saturate the UI thread or produce repeated full-list sorts/renders. Microsoft.Data.Sqlite executes its async database methods synchronously; merely dropping an `await` does not move the scan off the Blazor dispatcher. Use an owned background worker with bounded work and explicit UI publication. See [Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).

This removes full-library loading from the opening milestone. It **does not eliminate eventual full-catalog memory consumption** if the background path still retains all full records. It is the shortest route to a useful first page, followed by option 2 if large-library memory remains unacceptable.

Do not substitute `ORDER BY rowid DESC LIMIT 49` for the current `ImportedAt` ordering: commit order and timestamps are not guaranteed to agree. A bare `LIMIT` also cannot implement the existing encrypted deletion/filter rules.

### 2. Separate compact browse summaries from full record details

**Preferred structural follow-up for sustained use at hundreds of thousands or millions of items.**

Keep compact, encrypted browse fields separate from workflows, raw metadata, long descriptions, paths not needed by the grid, and full generation text. Load details only for selection, export, analysis, or other operations that need them. Existing keyed single-record reads provide part of the foundation.

An initial summaries scan still scales with N, but reduces bytes read, JSON string allocation, and retained memory. A later encrypted page index can bound initial work independently of library size. Keep full-library text search as a separately prepared capability; current search includes raw metadata and uses case-insensitive substring matching, so a new search index must preserve that behavior or explicitly change the product contract.

Introduce a distinct browse model. Do not deserialize partial data into `ImageRecord` and then persist it: that risks erasing metadata on edits. Write record/detail/summary changes in one transaction. Version and backfill summaries in resumable chunks, with the authoritative record retained until compatibility is verified.

All derived metadata remains encrypted. A plaintext SQLite index on timestamps, names, tags, or ratings creates a new disclosure surface. Decide the encryption-preserving ordering/index design before introducing SQL paging; ordinary SQL indexes cannot sort fields that exist only inside opaque encrypted blobs.

Separating thumbnail storage may improve record-scan locality, but the supplied data does not isolate that benefit. Treat it as a separate experiment rather than bundling a large database rewrite into the first-page change. On this nearly full disk, avoid a wholesale catalog copy or `VACUUM` as part of investigation.

### 3. Tune the existing full-load implementation

Bounded parallel decrypt/deserialize, reusable zeroed buffers, and connection/migration improvements are experiments with smaller implementation scope. They retain the requirement to read and materialize the entire library before opening unless combined with option 1.

Try a bounded producer/consumer pipeline with a small worker count, preserve result ordering, and cap in-flight bytes. More workers can worsen allocation and memory pressure. Pooled plaintext must be zeroed on success, failure, and cancellation, and never published after its lease ends. Measure changes independently so a cache-warming effect is not mistaken for a code improvement.

A no-schema experiment can deserialize a separate lightweight browse DTO that skips heavy strings. It still reads/decrypts/authenticates every full encrypted record, so it can reduce allocation without eliminating full-record I/O. It is useful for isolating the cost of retaining raw metadata before committing to option 2.

## Recommended implementation sequence

1. **Make the comparison reliable.** Preserve the supplied baseline. Record effective GC mode, GC pause duration, live heap, and supported macOS memory/process-I/O data; render unavailable measurements as unavailable. Separate query stepping, blob copying, and other loop work. Match the web app's GC configuration, while keeping different modes in separate report series.
2. **Add useful milestones shared with the UI.** Measure `key_unlocked`, `first_page_ready`, `first_page_rendered`, `thumbnails_ready`, and `catalog_ready`. Browser rendering requires UI instrumentation; an in-process Core timer cannot establish it. Keep thumbnail time out of the main opening target.
3. **Implement option 1.** Add a generation-bound browse-opening API, transactional encrypted first-page index, batched page-record read, and explicit UI background-loading state. Use a configurable initial page size and preserve the current default sort and active-image rules.
4. **Validate on the existing gallery.** No further synthetic generation is needed. Measure a bounded subset for diagnostics and the existing 350k gallery once resource headroom is appropriate. A third size adds evidence, but it must be labelled with the actual count and GC mode rather than mixed into old series. Do not copy the 49.5 GB catalog just to obtain that point.
5. **Implement option 2 if background memory/latency remains high.** The long-term target is bounded initial work and on-demand full details, not merely moving a very large allocation burst behind the grid.

Proposed acceptance goal: warm passphrase-submit to first usable page **p95 below 2 seconds** at 100k, 200k, and 350k. This is a product target to test, not a prediction from the old data. Opening work should read only the index, settings, and first-page records. Search/count readiness and background memory should be reported independently.

## Correctness checks for the first-page change

- Cached/indexed first-page order and membership match the authoritative default view after imports, edits, deletion marks, physical removal, restore, and concurrent requests.
- Missing, stale, corrupt, or obsolete indexes never silently return incorrect data; authentication failures follow the application's existing integrity-error behavior.
- Selection, viewing, rating, and other enabled first-page actions operate on authoritative records. Global operations are unavailable or explicitly pending until their data is ready.
- Auto-lock, panic, vault switching, and disposal cancel or invalidate background work. Old-generation results cannot populate a newly opened or locked gallery. Plaintext caches are released when locking.
- Later-record corruption is surfaced when background loading or access verifies that record. Lazy opening changes when full-catalog integrity failures are discovered; opening a page does not certify every other row.
- A one-time index rebuild is visible and recoverable. Failed mutations cannot leave a cache revision claiming data that was never committed.
- UI responsiveness, first usable page, thumbnail completion, full search/count readiness, peak resident memory, and GC pauses are measured separately.
