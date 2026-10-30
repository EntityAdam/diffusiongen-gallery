# 01: Context and findings

This document has two clearly separated parts:

- **Part A: facts.** Read directly from the code at `main@5671143`, with file and line references.
- **Part B: hypotheses.** Reasoned guesses about where the 3 minutes go. None of them has been measured. The
  harness and the telemetry exist to confirm or reject them.

---

## Part A: facts (from code)

### A1. What the UI does on "Unlock"

`src/Gallery.Web/Components/Pages/Home.razor`, `UnlockAsync` (about lines 1167–1192) runs:

1. `Gallery.UnlockWithPassphraseAsync(secret)` (or the recovery-phrase variant)
2. `Gallery.LoadPreferencesAsync()`
3. `Gallery.ConfigureAutoLock(...)` and a JS interop call
4. `ReloadAsync()` (about lines 1204–1223), which runs:
   1. `Gallery.LoadAsync()`, which returns **all** records
   2. `Gallery.LoadMediaStorageOptionsAsync()`
   3. `Gallery.LoadIngestionOptionsAsync()`
   4. `images.ToDictionary(...)` and `UpdateDuplicateGroups()`, which are in-memory. `duplicateScan` is null on a fresh unlock.
   5. `Gallery.SettingsAsync()`
   6. `Gallery.CachedStorageOverviewAsync()`
   7. `LoadThumbnailsAsync()`, which calls `Gallery.ThumbnailAsync(id)` for each item on the first page. The page size is 12, or
      49 in compact mode (line 1011).

The user perceives all of this as "unlock". **The harness must measure this whole sequence, not only key
derivation.**

### A2. Key derivation

- `GalleryService.Lifecycle.cs` lines 84–96: `UnlockWithPassphraseAsync` calls `VaultStore.UnlockWithPassphraseAsync`.
- `VaultStore.cs` lines 148–186 read `vault.json` and run PBKDF2.
- `VaultCrypto.cs` line 9: **`Iterations = 600_000`**, PBKDF2-SHA256 (line 17). This is a constant cost,
  independent of gallery size.

### A3. Catalog load at unlock

`GalleryService.cs` lines 314–335 (`LoadAsync`, then `LoadGalleryWithKeyAsync`), while holding `galleryStore.Gate`:

1. `ReadRecordsAsync(key)` runs `SELECT id, record FROM images` (`GalleryStoreCommandFactory.cs` line 59). For
   **every row** it then:
   - validates the envelope and decrypts it with AES-GCM (`VaultCrypto.Decrypt`), and
   - runs `JsonSerializer.Deserialize<ImageRecord>` (`GalleryStore.cs` lines 204–215 and 424–441).
2. `BackfillFingerprintsAsync`: `SELECT id, record FROM images WHERE fingerprint IS NULL`. There is a
   unique index on `fingerprint` (`M0002_Fingerprint.cs`), so this is index-assisted.
3. `BackfillGenerationAsync`: re-parses ComfyUI metadata for records where `MetadataVersion < ComfyMetadata.Version`,
   with **one `UpdateAsync` per stale record**. Each call opens its own connection.

After releasing the gate:

4. `ScheduleMediaIndexAsync` (`GalleryService.DriveOperations.cs` lines 127–138) runs once per vault. When the
   marker is missing, it enqueues recursive `ScanDirectory` operations.
5. `driveOperationPlayer.Start()` starts background drive work (directory scans, deletes). This runs
   concurrently with the rest of the UI sequence and can compete for I/O.

`LoadAsync` returns a fully materialised `List<ImageRecord>`. The UI keeps it as `images` and builds
`imagesById`.

### A4. What a record contains

`Models/ImageRecord.cs` has about 30 properties. Several of them are unbounded strings:

- `Metadata`: raw embedded metadata. For ComfyUI PNGs this includes the `prompt` and `workflow` JSON text chunks
  (`ComfyMetadata.cs` lines 12–13 and 114).
- `Description`, `UserDescription`, `Tags`, `SourcePath`, plus the nested `GenerationInfo` (prompt and negative prompt).

So every unlock decrypts and deserialises each item's full workflow JSON, even though the grid shows only a
page of thumbnails.

### A5. Catalog schema

From `Gallery.Migrations/Scripts/M0001_Baseline.sql` and `M0002_Fingerprint.cs`:

```
images(id TEXT PRIMARY KEY, record BLOB NOT NULL, thumbnail BLOB NOT NULL, fingerprint BLOB)
```

- Records and thumbnails are stored **in the same rows**. In this B-tree, the record blobs and the thumbnail
  overflow pages are interleaved across the file.
- Thumbnails are PNGs of at most 384×384, encoded at `PngCompressionLevel.Level1` (`GalleryService.cs` lines 715–731), then encrypted.
- `images.id` is a `TEXT` primary key, so this is a rowid table with a separate autoindex.

### A6. Connection behaviour

`GalleryStore.OpenAsync` (`GalleryStore.cs` lines 49–68) runs on **every** store call:

- `new SqliteConnection(... Pooling = false ...)`
- `PRAGMA synchronous=FULL`
- `PRAGMA journal_mode=WAL` (or `DELETE` on network drives)
- **`GalleryDatabase.Upgrade(connection)`**. This builds a DbUp upgrader, scans the embedded scripts, and
  runs `PerformUpgrade()` **inside a transaction**. `GalleryDatabase.cs` comments that Microsoft.Data.Sqlite starts an
  immediate transaction.

A UI unlock opens at least 8 connections. Each first-page thumbnail adds one more, so 12–49 more.

### A7. Telemetry that already exists

| Area | Mechanism | Location |
|------|-----------|----------|
| Ingestion | `IngestionTimingReport` with per-stage count/total/mean/p50/p95/max, delivered through the `reportTimings` callback on `ImportFolderAsync` | `IngestionTimings.cs`, `GalleryService.cs` lines 962–1022 |
| Backup | `ActivitySource("Gallery.Core.Backup")` with `*_elapsed_ms` tags | `GalleryService.Backup.cs` lines 15–16, 107, 273 |
| Vault open/unlock | **None** | none |

`IngestionTimings` and `DescribeDrive` follow a privacy rule: only counts, sizes, durations and drive kinds are recorded,
never file names, folder names or paths (`IngestionTimings.cs` lines 36–38 and 140). New telemetry must follow the same rule.

### A8. Existing large-library test

`src/tests/Gallery.Tests/LargeLibraryBackupMeasurementsTests.cs` is opt-in through `GALLERY_LARGE_LIBRARY_TEST=1`.
It imports 100, 500 or 1000 tiny PNGs through `ImportAsync` and measures incremental backup only. It does not
measure unlock, does not use realistic file sizes, and does not use folder ingestion.

### A9. Useful public APIs for a harness

- `GalleryService.Create(vaultRoot, GalleryRuntimeOptions { StateDirectory = ... }, HttpClient)` creates a
  service the same way the app does.
- `SelectNewVaultAsync(dir)`, then `CreateVaultAsync(passphrase, confirmation, name, MediaStorageOptions { MediaDirectory })`
- `OpenExistingVaultAsync(dir)`, `UnlockWithPassphraseAsync`, `LoadAsync`, `ThumbnailAsync`, and so on.
- `ImportFolderAsync(folder, FolderSearchScope.Descendants, sourceRetention: SourceRetention.DeleteAfterVerification,
  reportTimings: r => ..., options: new IngestionOptions { Workers, CatalogBatchSize })`
- `Gallery.Core` grants `InternalsVisibleTo` only to `Gallery.Tests` and `Gallery.TestSupport`.

### A10. The user's numbers (user-reported, not measured)

- 28,000 files and 67 GB of media: **about 2.4 MB per media file on average**.
- A 5 GB catalog: **about 180 KB of catalog per media item** (record, thumbnail and index together).
- About 3 minutes for a fresh, uncached unlock: **about 6.4 ms per item**.

---

## Part B: hypotheses (not measured)

Each hypothesis lists what would confirm or reject it. The telemetry in doc 03 is designed to tell them apart.

| ID | Hypothesis | Why we suspect it | Confirm or reject with |
|----|-----------|------------------|-----------------------|
| H1 | **Full-catalog decrypt plus JSON deserialise dominates.** The cost scales with rows × record size. | A3 and A4: every unlock materialises every record, including workflow JSON that is not needed for the grid. | `gallery.load.decrypt_ms`, `deserialize_ms` and `record_bytes` vs. total time. Check the slope against row count and against record bytes. |
| H2 | **Cold random I/O on `gallery.db` dominates the "uncached" case.** Record pages are interleaved with thumbnail overflow pages across a 5 GB file, so a cold scan reads far more than the record bytes and reads them out of order. | A5, and the user stresses "fresh uncached". | Process I/O read bytes and the `query_read_ms` split, cold vs. warm, at the same N. A large cold-minus-warm gap points to H2. |
| H3 | **Per-connection DbUp, PRAGMAs and unpooled opens add fixed overhead per call.** That overhead multiplies with the number of thumbnails and settings reads. | A6 | `catalog.connection.open_ms` and `migration_ms` histograms, plus the count of opens per unlock. |
| H4 | **PBKDF2 (600k iterations) is noticeable but constant.** It is probably 0.3–1 s and not the 3 minutes. | A2 | `vault.unlock.kdf_ms` stays flat across sizes. |
| H5 | **Background drive work started at unlock competes for I/O** (media-index scans and queued deletes). | A3, step 5 | Time-to-player-idle, and unlock time with and without pending drive operations. |
| H6 | **One-off migrations can turn a single unlock into a very slow one**: per-record `UpdateAsync` in `BackfillGenerationAsync` when `ComfyMetadata.Version` changes. | A3, step 3 | `backfill_generation_count` and `backfill_generation_ms`. Not part of the steady-state 3 minutes, but a scale risk. |
| H7 | **UI-side costs at large N**: `ToDictionary`, filter and sort in `Browsing.Images`, and render. They are small at 28k but grow with N. | A1, step 4 | Out of harness scope at first. Add a Blazor-level or Playwright measurement later (doc 04, Phase 7). |
| H8 | **Memory is a ceiling at 5 TB.** About 1M fully materialised `ImageRecord`s with metadata strings may need many GB of managed heap. | A3 and A4 | `GC.GetTotalAllocatedBytes`, peak working set and gen-2 GC count per open, vs. N. |

### Scale extrapolation (inference from A10; not measured)

| Media size | Files at 5 MB average (the 2–8 MB spec) | Files at 2.4 MB average (user's real mix) | Catalog at about 180 KB per item |
|-----------:|----------------------:|---------------------:|---------------------:|
| 67 GB (today) | n/a | 28,000 | 5 GB |
| 250 GB | 50,000 | 104,000 | 9–19 GB |
| 500 GB | 100,000 | 208,000 | 18–37 GB |
| 1 TB | 200,000 | 417,000 | 36–75 GB |
| 5 TB | 1,000,000 | 2,083,000 | 180–375 GB |

**Note (revision 2).** A 2–8 MB spec would yield about half as many files per TB as the real library. If H1, H2 or H3 hold,
unlock cost tracks file count, so that would be a *gentler* test than real data. **Resolved:** the targets are now item counts
(100k, 200k and 400k, about 250 GB, 500 GB and 1 TB of real media at 2.4 MB per file) using ~1 MB files. Large-file cost moves to a
separate large-image suite (doc 02, section 7). Results are still reported per file and per catalog byte.
