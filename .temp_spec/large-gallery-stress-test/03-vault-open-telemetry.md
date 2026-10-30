# 03: Vault-open telemetry (to add to `Gallery.Core`)

Fact: no telemetry exists for open or unlock today (doc 01, A7). The ingestion and backup code show two existing patterns:

- **Ingestion**: a bespoke `IngestionTimingReport`, delivered through a callback parameter.
- **Backup**: `ActivitySource` with `*_elapsed_ms` tags. A test or the harness subscribes with `ActivityListener`
  (`LargeLibraryBackupMeasurementsTests.cs` lines 184–215).

**Decision: follow the backup pattern** (`ActivitySource` plus tags), and add a small `Meter` for per-connection counters.
Reasons:

- It is zero-cost when no listener is attached: `StartActivity` returns null.
- It needs no API change to `UnlockWithPassphraseAsync` or `LoadAsync`.
- The same data later works with OpenTelemetry or `dotnet-counters` in the real app, so the user can capture their real
  3-minute unlock without the harness.

## Privacy rule (same as `IngestionTimings`)

Only counts, byte totals, durations, booleans and drive kinds. **Never** ids, names, paths, prompts or key material.
Add a unit test that asserts this (see the acceptance criteria).

## Sources

```csharp
public const string VaultOpenActivitySourceName = "Gallery.Core.VaultOpen";   // on GalleryService, like BackupActivitySourceName
private static readonly ActivitySource VaultOpenActivitySource = new(VaultOpenActivitySourceName);

public const string CatalogMeterName = "Gallery.Core.Catalog";
// in GalleryStore (internal) or a small internal static CatalogMetrics class
```

## Activities and tags

### `vault.unlock`

Started in `UnlockWithPassphraseAsync` and `UnlockWithRecoveryPhraseAsync` (`GalleryService.Lifecycle.cs` lines 84–110).

| Tag | Meaning |
|-----|---------|
| `vault.unlock.method` | `passphrase` or `recovery` |
| `vault.unlock.gate_wait_ms` | wait on `lifecycleGate` |
| `vault.unlock.header_read_ms` | read and validate `vault.json` |
| `vault.unlock.kdf_ms` | PBKDF2 (`VaultCrypto.Derive`) |
| `vault.unlock.unwrap_ms` | unwrap the key and check it |
| `vault.unlock.history_save_ms` | `history?.Save` |
| `vault.unlock.elapsed_ms` | total |

The KDF and unwrap happen inside `VaultStore.UnlockCredentialAsync` and `UnwrapVaultKey` (`VaultStore.cs` lines 148–186).
Either pass a small internal timing struct out, or start child activities there. Prefer **child activities**
(`vault.unlock.kdf`), so `VaultStore` does not need a new return type.

### `gallery.load`

Started in `LoadGalleryWithKeyAsync` (`GalleryService.cs` lines 320–335).

| Tag | Meaning |
|-----|---------|
| `gallery.load.gate_wait_ms` | wait on `galleryStore.Gate` |
| `gallery.load.record_count` | rows returned |
| `gallery.load.record_bytes` | sum of the encrypted `record` blob lengths |
| `gallery.load.connection_open_ms` | `OpenAsync` total for the read: open, pragmas and migration |
| `gallery.load.query_read_ms` | time inside `ExecuteReaderAsync` and `ReadAsync` and blob materialisation, excluding decrypt and deserialise |
| `gallery.load.decrypt_ms` | sum of `VaultCrypto.Decrypt` for records |
| `gallery.load.deserialize_ms` | sum of `JsonSerializer.Deserialize<ImageRecord>` |
| `gallery.load.backfill_fingerprint_count` / `_ms` | rows backfilled, and the time |
| `gallery.load.backfill_generation_count` / `_ms` | stale records re-parsed and updated, and the time |
| `gallery.load.schedule_media_index_ms` | `ScheduleMediaIndexAsync` |
| `gallery.load.media_index_scheduled` | bool: true if this unlock queued the first full index scan |
| `gallery.load.elapsed_ms` | total |

Implementation note: `ReadImageRecordsAsync` (`GalleryStore.cs` lines 204–215) is a static loop. Accumulate the timings with
`Stopwatch.GetTimestamp()` deltas into a `ref struct` or a small class passed in, only when
`Activity.Current is not null` (or `activity?.IsAllDataRequested == true`). This avoids per-row overhead when no one is listening.

### `gallery.settings.read`

This is optional; a counter may be enough. It covers `LoadPreferencesAsync`, `LoadMediaStorageOptionsAsync`, `LoadIngestionOptionsAsync`,
`SettingsAsync` and `CachedStorageOverviewAsync`. Tag `gallery.settings.id`; settings ids are constants such as
`storage-overview`, not user data, so they are allowed. Tag `elapsed_ms` too.

### `gallery.thumbnail.read`

Do **not** create an activity per thumbnail, which is too noisy. Use the meter instead (below).

## Metrics (`Meter` "Gallery.Core.Catalog")

| Instrument | Type | Unit | Recorded in |
|------------|------|------|-------------|
| `gallery.catalog.connection.opens` | Counter<long> | {open} | `GalleryStore.OpenAsync` |
| `gallery.catalog.connection.open.duration` | Histogram<double> | ms | `OpenAsync`: `OpenAsync()` and pragmas |
| `gallery.catalog.migration.duration` | Histogram<double> | ms | around `GalleryDatabase.Upgrade(connection)` |
| `gallery.catalog.thumbnail.read.duration` | Histogram<double> | ms | `ReadThumbnailAsync` |
| `gallery.catalog.thumbnail.bytes` | Counter<long> | By | `ReadThumbnailAsync` |

The harness subscribes with `MeterListener` and reports the count, sum, p50 and p95 per open run.

## Acceptance criteria (Phase 1)

1. Build is clean. All existing tests pass.
2. New tests in `src/tests/Gallery.Tests/VaultOpenTelemetryTests.cs`:
   - Unlocking a `TestVault` with an `ActivityListener` attached emits `vault.unlock` with all its documented tags, and
     `kdf_ms` > 0.
   - `LoadAsync` on a vault with 3 imported images emits `gallery.load` with `record_count == 3` and `record_bytes > 0`.
   - A `MeterListener` sees `gallery.catalog.connection.opens` increase.
   - **Privacy test**: across all emitted tags and measurement tags, no value contains the vault root, any record id,
     any record name, or a path separator followed by a known folder name.
   - With no listener attached, `LoadAsync` still succeeds. A smoke test checks that no activity was created.
3. No change to public method signatures, apart from the new `public const string` names.
4. Overhead with no listener attached: per-row work adds at most one null or `IsAllDataRequested` check.

## Using it outside the harness

After Phase 1, the user can capture their **real** 3-minute unlock with no harness, using
`dotnet-trace collect --providers Microsoft-Diagnostics-DiagnosticSource:...` or `dotnet-counters monitor --counters Gallery.Core.Catalog`.
Add the exact commands to the harness README in Phase 5. This is the most valuable first data point: it replaces
hypotheses H1–H5 with measurements on the real 28k gallery before any synthetic data exists.
