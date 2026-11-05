# SQLite catalog design with application-level encryption

Status: earlier proposal, 2026-10-30. The encrypted-component design below remains unimplemented. The subsequent experiment adds an optional Turso adapter behind catalog ports, with native page encryption and typed tables, while preserving the existing SQLite adapter. See the [implemented adapter and configuration](../src/Gallery.Web/README.md#experimental-turso-catalog-adapter). No existing vault is converted automatically.

## Recommendation

Keep Microsoft.Data.Sqlite and the current native SQLite provider. Replace `images.record` with independently encrypted components, a compact encrypted browse projection, and on-demand detail reads. Retain authenticated AES-256-GCM, the existing vault master key and recovery flow, existing public item IDs, and encrypted media files.

Use small, versioned binary payloads for structured components and encrypted UTF-8 for individual text/document fields. The complete `ImageRecord` is no longer serialized to JSON. Raw metadata remains a document in its own encrypted table because ComfyUI/EXIF/video structures are variable and must round-trip exactly.

Do not put timestamps, filenames, paths, ratings, deletion flags, tags, collection names, models, or prompts into plaintext SQLite columns. Ordinary SQL cannot sort/filter these encrypted values. The proposed query strategy is:

1. Open the first page using the existing encrypted ID cache and page-sized browse reads.
2. Stream/decrypt compact browse projections into a session-owned in-memory query index, then enable global sorting, filters, counts, and facets.
3. Fetch details, annotations, prompts, metadata, and thumbnails only for the actions that need them.

The global index has O(N) preparation time and O(N) compact-summary memory. This removes raw metadata and full details from ordinary browsing; it does not promise constant-time global readiness or memory independent of item count. A cache miss still requires a compact-summary scan to choose the first page. Full-metadata text search remains a separate, potentially expensive operation.

The repository's 350k-item measurements read 13.49 GB of encrypted records during the full load, versus 392,883 bytes for the cached first page. The present background load eventually retains full records. These findings motivate separating browse and detail data; they do not predict a speedup for this design. See [the measured analysis](performance/2026-10-30-vault-open-analysis.md).

## Persistent schema

Use an integer `media_id` for compact joins and retain existing string `Id` values as unique public identities. Store only opaque identifiers, ciphertext, the existing keyed import fingerprint, and operational counters in ordinary SQLite columns. Bind every ciphertext to the public item ID and component purpose using authenticated additional data (AAD).

| Table | Authoritative contents after decryption | When read |
|---|---|---|
| `media` | `core`: names, import timestamp/offset, dimensions, original size, media/content types, duration, favorite/deletion/review flags, rating | Editing core fields; full details |
| `media_browse` | Derived compact projection described below | First page and global summary preparation |
| `media_locations` | Stored path, source path/folder, source-created timestamp/offset | Preview, export, moves, restore |
| `media_annotations` | Independently encrypted description, user description, original tags text, analysis model | Annotation edits, analysis, detail/search |
| `media_organization` | Ordered collection names, preserving per-item spelling | Organize and details |
| `media_generation` | Small parameters block plus separately encrypted prompt and negative prompt | Generation details, exact prompt confirmation/groups |
| `media_metadata` | Exact metadata text plus an encrypted parser-version block | Metadata inspection, workflow export, parser backfill, metadata search |
| `media_hashes` | Source SHA-256 and content SHA-256 | Verification and deduplication support |
| `media_thumbnails` | Unchanged encrypted thumbnail envelope | Visible-page thumbnail requests |
| `media_perceptual_hashes` | Cached perceptual hash, algorithm version, source content hash, or encrypted missing marker | Duplicate scans |
| `catalog_state` | Public revision and encrypted format/migration state | Cache invalidation and conversion |

All one-to-one component rows exist for every migrated item, including empty metadata/annotations and null generation. Null generation is represented *inside* the encrypted parameters block, independently of a present generation with empty strings. Likewise, a missing perceptual hash is represented inside its encrypted payload. This avoids exposing generation/analysis/cache presence merely through optional row existence.

The existing `settings`, `drive_operations`, `media_index`, and `storage_overview_state` remain, with their encrypted values and keyed path identifiers. Queued file deletion must survive removal of a catalog item: do not cascade live-media deletion into the durable queue.

### Target DDL

This is a schema sketch for review, not a production migration. Enable and verify foreign keys before transactions on every connection. They cannot be enabled effectively midway through a transaction. [SQLite foreign keys](https://www.sqlite.org/foreignkeys.html).

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE media (
    media_id INTEGER PRIMARY KEY,
    id TEXT NOT NULL UNIQUE,
    core BLOB NOT NULL,
    import_fingerprint BLOB UNIQUE
        CHECK (import_fingerprint IS NULL OR length(import_fingerprint) = 32),
    row_version INTEGER NOT NULL DEFAULT 1 CHECK (row_version > 0)
) STRICT;

CREATE TABLE media_browse (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    payload BLOB NOT NULL
) STRICT;

CREATE TABLE media_locations (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    payload BLOB NOT NULL
) STRICT;

CREATE TABLE media_annotations (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    description BLOB NOT NULL,
    user_description BLOB NOT NULL,
    tags_text BLOB NOT NULL,
    analysis_model BLOB NOT NULL
) STRICT;

CREATE TABLE media_organization (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    payload BLOB NOT NULL
) STRICT;

CREATE TABLE media_generation (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    parameters BLOB NOT NULL,
    prompt BLOB NOT NULL,
    negative_prompt BLOB NOT NULL
) STRICT;

CREATE TABLE media_metadata (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    parser_state BLOB NOT NULL,
    document BLOB NOT NULL
) STRICT;

CREATE TABLE media_hashes (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    payload BLOB NOT NULL
) STRICT;

CREATE TABLE media_thumbnails (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    encrypted_bytes BLOB NOT NULL
) STRICT;

CREATE TABLE media_perceptual_hashes (
    media_id INTEGER PRIMARY KEY REFERENCES media(media_id) ON DELETE CASCADE,
    payload BLOB NOT NULL
) STRICT;

CREATE TABLE catalog_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    format_state BLOB NOT NULL
) STRICT;
```

Initialize `catalog_state.format_state` with authenticated ciphertext during unlocked initialization, rather than a plaintext SQL default. The core, browse, format-state, and other component encodings have versions inside their encrypted contents. Verify STRICT-table support against the existing bundled native SQLite before implementing the DDL.

No index on encrypted `core`/`payload` values is useful for plaintext ordering. The primary keys, public-ID unique index, and existing-style import-fingerprint unique index are the necessary persistent indexes initially. Do not add an unkeyed prompt hash or a deterministic encrypted rating index. No new blind indexes are needed for this proposal.

### Why these encryption boundaries

Encrypt tightly related small scalar fields together to keep crypto and envelope overhead reasonable. Avoid dozens of AES-GCM calls per item for booleans and numbers. Separate long text and independently edited fields so a note edit does not rewrite an analysis description, and a favorite change never rewrites workflow metadata or thumbnails.

`media.core` is deliberately a small typed scalar block; it excludes paths, descriptions, tags/collections, prompts, and raw metadata. It is not the old record moved to a differently named column. `media_browse` is a disposable query projection, not the authoritative full record.

The new layout still exposes table structure, item linkage, row/update activity, existing keyed-fingerprint equality, and component ciphertext lengths. Splitting one blob reveals component lengths separately; do not claim identical leakage to the old layout. Encrypt empty values too. Padding would be a separate product/security decision with storage cost, not a prerequisite silently added to this work.

### Browse projection contents and ownership

Include only fields needed for existing global filters, sorting, counts, and facets:

- Core names and scalar fields: imported time, dimensions, original bytes, type, duration, flags, rating.
- Source folder and stored directory; full source/stored filenames and source-created time stay in locations.
- Analysis model and `has_description` derived specifically from `Description.Length > 0`.
- Parsed tags using current trim/comma/case rules, and ordered collection names.
- Generation model/kind, presence and workflow/API flags, and a digest of the exact positive prompt.

Do not include raw metadata, full descriptions/notes, positive/negative prompts, thumbnails, or media bytes. Card prompt text and annotation excerpts are page-local detail requests. The prompt digest is stored *inside* the randomly encrypted projection, never in a plaintext hash column; verify full prompt text for exact-match candidates. Missing generation and empty prompt preserve current grouping/filter behavior.

Core fields belong to `media.core`; every other browse field is derived from the relevant authoritative component. A transaction updates the changed component and its browse projection together. Derive projection values in the store/domain layer instead of trusting caller-supplied projections. Rebuild projections with an explicit codec/projection version and resumable background work. A stale version must trigger a safe rebuild/pending state, never incorrect global results.

Preserve exact collection ordering and per-item case in the encrypted organization block. Keep original `Tags` text in annotations for editing/export and cross-field search; parsed tag membership is derived. Avoid plaintext persistent tag/collection dictionaries or membership tables: they would disclose item relationships and label frequency. Normalize those relationships only in the unlocked in-memory index.

### Encoding and encryption contract

Use an established binary serializer with explicit field identifiers, such as CBOR, behind dedicated component codecs. Finalize the library and encoding rules in a short implementation spike. Do not serialize entire domain objects implicitly. Define codec versions, required fields, absent/null behavior, unknown-field handling, and byte/string/depth limits. Reader failures must not silently produce empty fields.

Use direct UTF-8 for descriptions, notes, tags, analysis-model strings, prompts, and raw metadata, then encrypt each independently. Preserve empty strings, null-generation distinctions, exact metadata text, timestamp offsets, and DateTimeOffset tick precision. Keep the `MetadataVersion` in encrypted `parser_state`, separate from the large document, so parser-version checks do not read every metadata document.

Continue using the current AES-GCM envelope with random nonces and existing key/session handling. Define distinct versioned purposes, for example `catalog:v2:core:{id}`, `catalog:v2:browse:{id}`, `catalog:v2:annotation:description:{id}`, and `catalog:v2:metadata:{id}`. Other components/fields require their own purposes. Keep `thumbnail:{id}` and `image:{id}` unchanged so existing thumbnail and media ciphertext can be copied verbatim. Row versions support concurrency; they are not an anti-replay guarantee.

Keep source `Sha256` and `ContentSha256` distinct inside the encrypted hashes block. The current import fingerprint is keyed from **source `Sha256`**, despite misleading existing parameter names. Preserve that deduplication behavior and uniqueness constraint. Encode legacy empty hashes explicitly; do not fabricate missing values or merge source/content hashes. Keep perceptual `ulong` hashes as eight bytes with a specified byte order inside their encrypted payload.

## Unlocked query index and first-page behavior

The first implementation keeps the current first-page cache pattern in encrypted settings: versioned IDs in newest-first all/active/marked order, revision and total count. Read cache, revision, and requested browse rows in one SQLite read snapshot. Resolve only page-sized locations/details on user selection or page rendering. Never hydrate full records merely to display the first page.

If the cache is missing/stale, scan only browse projections and retain a bounded top-K heap to determine the initial page and cache membership. This is still O(N) time, but does not require retaining all projections or reading metadata. Authentication failures follow the existing integrity-error behavior; a missing derived projection can be rebuilt, while unexplained corruption must be surfaced.

After first-page publication, build a generation-owned SQLite `:memory:` database from a bounded decrypt/decode stream of browse projections. Use one owner and serialized access; keep the same connection alive for its session. It contains typed summary columns plus normalized tag/collection membership tables, with indexes for deletion/import-time, rating, name, size, and membership lookups. No persistent plaintext query database, FTS files, or exported temporary cache.

Use `temp_store=MEMORY`, disallow disk attachments/exports, verify native temp-store behavior with representative large sorts, and exclude plaintext data from logging. Bound transient batches, SQLite cache/heap growth, page caches, and query concurrency. The complete in-memory summary index still grows with N and variable names/tags/collections; measure it and enforce a budget. If the budget is exceeded, use explicit streamed queries/counts with bounded top-K state and pending/progress UI, or report that global preparation cannot complete. Never return partial global counts as complete.

Expose `MediaSummary`, `MediaDetails`, `GalleryPage`, `QueryPage`, `ReadDetails`, `ReadMetadata`, `ReadThumbnail`, and field-specific update APIs. Keep partial models distinct: never save a summary as a complete record. Replace `Home.razor`'s whole-library `List<ImageRecord>` and LINQ browse state with page results. Prompts, descriptions, and locations fetched for cards/previews have bounded caches.

Keep global features pending until the index is complete at a known catalog revision. A concurrent mutation during preparation must either be replayed through a serialized revision-ordered change stream or cause a restart; do not publish a mixed snapshot. Initially favor a consistent database snapshot plus a revision compare before publication, with retries and progress under ongoing writes. After durable commits, update the in-memory projection before marking its revision current; if that fails, invalidate/rebuild it. Never let in-memory results claim a newer revision than their contents.

SQL paging and counts operate on the *session* database. Persist no plaintext sort keys. Preserve page-number navigation with measured OFFSET queries initially, or adapt explicitly to cursor navigation. Stable sort ties use the migrated integer `media_id` in legacy insertion order. Date filters retain current host-local day/DST semantics, not UTC-midnight semantics or original-offset calendar dates.

Register a versioned collation using `StringComparer.OrdinalIgnoreCase` and functions matching the existing substring rules on the in-memory connection. Default SQLite `NOCASE` and `LIKE` are not equivalent for Unicode. Preserve the folder check across both source folder and stored directory, analysis status based on description presence, tag/collection comparison rules, and every current `GalleryFilter` predicate. [Microsoft collation documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/collation).

Lock, timeout, switch, and disposal revoke/cancel index construction, queries, and page/detail caches, then close the in-memory database when operations drain. Use generation checks before publication. Dispose plaintext structures and zero owned byte buffers where practical; retain the existing honest limitations concerning managed strings, swap, and process dumps.

## Full text and prompt operations

The existing general search is ordinal-ignore-case substring matching on exactly `Name + " " + OriginalName + " " + Metadata + " " + Description + " " + UserDescription + " " + Tags`. It permits matches across field boundaries. It does not independently include paths, negative prompts, or all generation fields. Preserve this behavior unless separately changing the product contract.

Apply inexpensive summary filters first, then stream/decrypt only the needed annotations/metadata for candidates in bounded batches. Preserve joined-field boundaries in the matcher; checking fields independently misses valid matches. Sort candidate IDs through the session index, stream matches for the requested page, and calculate complete counts separately. Empty search does not read metadata. Nonempty broad searches may still read the entire metadata corpus and require progress/cancellation. Do not silently truncate results.

Do not persist a plaintext FTS index. A memory-only trigram index is a later optional experiment: it can duplicate the largest text corpus and conflict with the memory goal, and its Unicode/short-query semantics are not the same as .NET contains. No new persistent HMAC n-gram or prompt index is included; those reveal additional equality/access information and deserve a separate decision.

Positive-prompt filtering/groups can use the encrypted projection's digest after unlock for candidates, then decrypt and compare authoritative prompts. Group lists should be paged; a digest is never sufficient proof of equality. Exact groups may require deferred prompt reads and should expose readiness independently of ordinary browsing.

## Transactions, revisions, and existing consumers

A core/annotation/location/generation/organization write checks and increments the parent `row_version`, updates only affected authoritative components, and updates the compact projection in one short transaction. A metadata-only edit updates its document/parser state, extracted generation where applicable, and affected browse hints atomically. Rating/favorite edits do not read metadata, descriptions, or thumbnails. Changes to projections must preserve every unrelated hint.

Imports commit the parent, required component rows, projection, thumbnail, fingerprint, and durable work together. Removal captures its authenticated file path into durable deletion work before deleting the parent and cascading components, in the same transaction. Deferred file operations must retain their encrypted payload after the media row disappears.

Trigger catalog revisions on all authoritative/projection changes, with separate filesystem-size cache invalidation. Replace existing triggers referencing `images.record`/`thumbnail` when legacy storage is removed. Multi-table writes may increment revisions more than once: remove arithmetic assumptions and read the actual revision. Preserve/revise the first-page index only when membership/order are provably unchanged; otherwise invalidate it transactionally. Keep total-count/page-cache completeness distinct from full query-index readiness.

Convert backup manifests, export, source deletion checks, moves, restore, duplicates, and parser backfills to bounded component streams or explicit reads. Do not hide the old full-record scan inside a convenience API. The perceptual-hash dictionary moves out of a whole-library settings blob into per-item encrypted cache rows.

SQLite connection initialization, schema inspection, physical snapshots, and ciphertext fingerprints remain keyless where they already are. Operations that decrypt or rebuild projections require existing generation-bound key leases. No database-key provisioning or provider replacement is introduced. Existing WAL/FULL durability and local-catalog rules remain.

## Migration and rollout

1. **Define codecs and behavior first.** Confirm bundled SQLite features, component field ownership, size limits, summary memory budget, and legacy round-trips. Add an explicit new vault/catalog format gate so older app versions reject migrated catalogs before opening their store or running DbUp. Keep passphrase/recovery key slots valid. Define new-format initialization separately from legacy automatic schema upgrades; do not destructively drop records in an ordinary connection-open migration.
2. **Build adapters and query paths.** Add schema scripts, codecs, field-specific transactions, required-child validation, revision triggers, compact-scan fallback, and session index. Cover all ImageRecord/GenerationInfo fields, including legacy JSON names `PngSha256`, `Favorite`, `MarkedForDeletion`, and `Reviewed` at the legacy boundary. Make backup/restore compatible before enabling conversion.
3. **Run an explicit resumable unlocked conversion.** With exclusive vault checkout and writes/drive work drained, build a new ordinary SQLite staging catalog containing ciphertext only. Keep the old catalog authoritative. Use bounded row-and-byte batches; authenticate/decrypt each legacy record, validate identity, split/encode/encrypt components, construct the projection, and copy unchanged thumbnails. Commit destination rows and conversion cursor together. Capture source format, identity/content fingerprint, revision, and schema identity in a durable receipt; reject changed sources on resume. Revision alone cannot identify a replaced source database.
4. **Preserve exact data and auxiliary state.** Do not reparse workflows just to migrate: preserve exact metadata and existing generation values. Preserve dates/offsets, names, paths, collection order/case, tags text, hashes, public IDs, and legacy rowid-based tie order. Copy settings, queue sequence/retry state, media index, and storage state. Replace the derived first-page cache with a new-format cache. Split the old perceptual cache in a bounded phase; if its one encrypted dictionary is too large to decode, explicitly invalidate/recompute that derived cache. Authentication or unsupported-data failures stop publication without coercing or dropping items.
5. **Verify and publish recoverably.** Compare all logical fields and unchanged thumbnail ciphertext, auxiliary rows/counts, required component existence, exact dedup behavior, and browse projections against a consistent source. Run integrity and foreign-key checks and explicit decrypt/authentication validation; SQLite integrity alone does not authenticate ciphertext. Remove any unused legacy baseline tables from the destination only through the explicit format-finalization path. At completion the destination has no `images.record`. Checkpoint/close catalogs before publication. Use a durable receipt to recover interruption between catalog replacement and header-format publication; keep a matched old header/catalog backup. Never rename a live WAL database.
6. **Roll out and measure.** Retire eager full-record UI loading, retain first-page cache behavior, and instrument summary preparation, search, counts, and memory separately. Backfills and global operations run outside the opening critical path. New writes after publication make the retained old catalog a historical backup rather than a lossless rollback destination.

Preflight space for the full staged catalog, its indexes/WAL, and retained original. Splitting blobs and removing JSON escaping do not make the retained raw metadata disappear. On a nearly full disk, require an appropriate staging/backup location; do not silently delete the source or run VACUUM to force the conversion. Cross-filesystem staging needs a verified recoverable copy-to-final step rather than an assumed atomic rename.

## Backup, restore, and tooling

The existing ordinary-SQLite snapshot mechanism can still copy ciphertext without keys; verify this with the new tables. Logical backup fingerprints must include every authoritative component and work with the new table order, while excluding intended derived/settings state consistently. Version the fingerprint/manifest format, and keep the old algorithm for validating existing full/incremental archives before migration. Restore validates an archive in its original format before conversion and path rewriting. Test old/new full and incremental chains, relocation, full moves, purge, and passphrase/recovery unlock.

The keyless read-only profiler remains possible: report browse/core/metadata/annotation/thumbnail ciphertext sizes separately, plus total SQLite object sizes, missing fingerprints, and queue state. Do not attempt semantic plaintext counts while locked or run migrations in the profiling connection. Recalibrate synthetic catalogs using per-component size distributions and distinguish summary-ready from full-search-ready metrics.

## Verification and acceptance

- Round-trip every field, null/empty distinction, timestamp offset/precision, raw metadata string, and ordered collection spelling. Validate binary codec limits, malformed data, unknown versions, wrong-purpose/ID ciphertext, wrong keys, and cancellation buffer cleanup.
- Compare old/new filters, sorts, groups, page jumps, and review behavior using Unicode, equal sort values, empty generation, image/video mixtures, deletion states, and local-date/DST boundaries.
- Exercise concurrent edits, optimistic conflicts, rollback, duplicate imports, multi-component/projection consistency, queue atomicity, revision changes during index build, cache misses, and mutation replay/invalidation.
- Inject crashes around conversion batch commits and catalog/header publication. Verify resume, source-change rejection, matched-format recovery, and old/new backup restore.
- Inspect DB/WAL/staging/temp outputs for sentinel names, tags, descriptions, ratings encoded as text, and workflow strings; verify no persistent plaintext query/FTS database or logged payloads. Check the selected native build's in-memory temporary-store behavior under large queries.
- Measure existing 100k/200k/350k catalogs with aligned cache/GC conditions: first page, thumbnail completion, compact index readiness, full-metadata search, allocations, peak resident memory, import throughput, and edit bytes read/written.
- Acceptance: first-page opening reads only its encrypted cache and page-sized projections; cache-miss work reads projections rather than metadata. Global preparation reads compact projections only. Rating/favorite changes never touch raw metadata/thumbnails. No active `images.record`, no full-record background materialization, and no plaintext persistent browse fields. O(N) summary memory and global-preparation time are measured and budgeted explicitly. The prior warm p95-under-two-seconds opening target remains a target to verify, not a prediction.
