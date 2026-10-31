# Turso stress ingestion slowdown

Date: 2026-10-31. Investigation of the supplied 100k/200k build with 50,000 generated files per harness batch, four workers, catalog commits of 16, and local Turso encryption.

## Finding at investigation time

The run was making progressively slower ingestion progress, rather than sitting in a demonstrated deadlock. No stress process was running when inspected at about 14:45 America/New_York. The primary scaling defect is in the experimental adapter's connection lifecycle: every catalog operation creates and disposes a native Turso database handle. Opening a handle against the accumulated multi-gigabyte WAL is expensive. The harness's very large batch and suppressed progress make that slowdown look like a stall.

This is an adapter defect in the initial implementation, not evidence that typed relational storage or encryption inherently requires this throughput. No production source changes were made during this investigation.

## Observed state

- Checkpoint: `Files=0`, `Batch=0`, `Pending=50000`. These are completed harness-batch counts, not committed database-row counts.
- Results directory: empty. The first ingestion row is appended only after all 50,000 files complete; open measurements start at 100,000, not at the first internal catalog commit.
- Actual committed records: **13,504**, counted on an encrypted APFS clone. Metadata, generation, and thumbnail tables also contain 13,504 rows each.
- Encrypted media: 13,504 `.dpng` files, about 14.24 GB in decimal units.
- Remaining/generated staging PNGs: 40,508, about 42.72 GB. Restarts regenerate deterministic sources before reconciliation, so this is not simply 50,000 minus the committed count.
- Main database: **1,918,439,424 bytes**; WAL: **2,282,945,592 bytes**; combined: 4,201,385,016 bytes.
- Available disk space: about 330 GiB at initial inspection. Disk exhaustion is not supported as the immediate cause.
- Media creation timestamps run from 11:01:14 to 14:42:07. The checkpoint was rewritten at 14:42 and staging files were rewritten through 14:43, consistent with an interrupted/restarted pending batch.

The media timestamps provide a throughput proxy, not a completed ingestion timing report:

| Local interval | New encrypted media files | Approximate files/s |
|---|---:|---:|
| 11:02–11:07 | 1,405 | 4.68 |
| 11:30–11:40 | 784 | 1.31 |
| 12:30–12:40 | 464 | 0.77 |
| 13:30–13:40 | 382 | 0.64 |
| 14:30–14:40 | 314 | 0.52 |

Across 21 complete ten-minute windows starting at 11:10, seconds per new media file track the midpoint existing-file count with R² approximately 0.997. This supports a growing per-operation cost. It is observational filesystem evidence, not a controlled engine benchmark or an ETA model.

## Isolated reproduction

To preserve the original, the harness and vault checkout locks were acquired while making APFS copy-on-write clones of the encrypted database, WAL, and wrapped-key header. The locks were then released. Only the diagnostic clone was opened with Turso or checkpointed. Credentials and plaintext keys were not written into the report or probe source. Original catalog/header sizes and nanosecond modification times were verified unchanged afterward.

The probe used the same published `Turso.Data.Common` 0.8.3 driver, AES-256-GCM, and domain-separated vault key as the adapter. It measured native handle opening, connection setup, a `COUNT(*)`, and closing independently.

| Clone condition | Handle open | Count query |
|---|---:|---:|
| Existing 2.28 GB WAL, first sample | 822.48 ms | 5.87 ms |
| Existing WAL, warm sample 2 | 355.76 ms | 6.33 ms |
| Existing WAL, warm sample 3 | 360.68 ms | 6.15 ms |
| Reused live handle, subsequent counts | No reopen | 0.012–0.102 ms |
| After truncating checkpoint, three reopens | 0.446–0.564 ms | 5.90–6.07 ms |
| Tiny encrypted control database | 0.222–0.283 ms | 0.034–0.039 ms |

A checkpoint of the clone took **21.15 seconds** and reduced its WAL to zero bytes. The original WAL was not checkpointed or deleted. Reopen time fell by roughly 700× on the clone; this is not a claim of a 700× ingestion speedup.

The measured effect isolates expensive native opening associated with the large WAL. Repeated WAL recovery/index reconstruction is the likely engine work; no native profiler trace was collected, so the exact internal CPU/I/O decomposition is not established.

## Why four workers and catalog batches of 16 do not prevent it

`TursoGalleryStore.ExecuteAsync` takes `databaseGate`, constructs a fresh `TursoConnection`, opens it, applies PRAGMAs, queries the format, executes the operation, and disposes the connection. The direct published driver's local `Open` constructs a native database handle; its SQLite facade's pooling behavior is not the API used here.

For each normal group of 16 imported files there are at least:

- 16 separate duplicate-check operations during preparation;
- one catalog commit operation;
- 16 separate catalog-verification operations before source deletion.

That is **33 native opens per 16 files**, serialized through the adapter. At the measured warm reopen cost, opening alone accounts for roughly 12 seconds per group at this catalog state, before SQL, decoding, thumbnails, encryption, media verification, and filesystem work. This is an illustrative cost calculation, not a measured stage breakdown of the original run.

The command's `--catalog-batch 16` batches writes, but it does not batch duplicate checks or verification reads. `--batch-files 50000` is a different setting: it controls the harness's generation/checkpoint/progress unit.

## Why progress and resume are misleading

The harness generates the entire pending batch before ingestion. Its production import progress callback checks cancellation but discards the progress text/counts. Its only completed-batch console message, timing result, and checkpoint advance occur after every source in the 50,000-file batch has finished.

On resume, `Pending` takes precedence over the new `--batch-files` value. The code regenerates every pending source, loads the existing catalog for reconciliation, and verifies committed media one item at a time before deleting regenerated sources. The same native-open cost therefore affects recovery. Repeated restarts can add considerable work without advancing the completed-batch checkpoint.

Do not delete the WAL, reset the checkpoint, or run cleanup against this partially built gallery as a performance workaround. The database contains committed records beyond the checkpoint. Reducing `--batch-files` on a resumed command does not split the already-pending 50,000-file batch.

## Recommended implementation changes from the investigation

1. Give Turso a reusable native connection/database lifetime scoped to an unlocked vault generation or an explicit leased ingestion operation. Serialize access as needed, stop admission on revocation, drain active operations, and close keyed resources before lock completion, relocation, snapshot publication, or disposal. A process-global keyed connection would violate the existing lifecycle contract. Merely changing a connection-string pooling flag is not sufficient evidence of reuse with this direct driver.
2. Add bounded WAL maintenance at safe transaction boundaries, with measured thresholds and explicit telemetry. Preserve FULL durability and source read-back verification. Do not trade integrity for throughput or blindly checkpoint every image.
3. Batch duplicate checks and catalog verification reads so a 16-item import commit does not imply 33 independent database opens. Prepared-command reuse is a secondary experiment after fixing handle lifetime.
4. Print throttled generation/import/recovery progress inside a harness batch, including completed/total and failures, without source paths or IDs. Persist diagnostic progress separately from the authoritative completed-batch checkpoint.
5. Improve pending-batch recovery so it does not unnecessarily regenerate valid existing staging files, and make recovery verification visible and cancellable.
6. Validate growth at several catalog sizes and record handle-open time/count, gate wait, WAL bytes, checkpoint duration, per-stage ingestion times, and memory. The previous tiny smoke runs verified correctness but did not expose this scaling defect.

For future fresh runs, use harness batches of 1,000–5,000 for earlier results and more frequent checkpoints. This improves observability/recovery granularity; it does not by itself fix the Turso handle/WAL scaling issue. No reliable completion ETA for 100k/200k follows from the current degrading run.

## Source references

- `src/Gallery.Core/Services/DataManagers/Turso/TursoGalleryStore.cs`: per-operation open/dispose and serialized database gate.
- `src/Gallery.Core/Services/GalleryService.cs`: duplicate checks, 16-item commit groups, and per-item verification.
- `src/perf/Gallery.Stress/Build/BuildCommand.cs`: pending-batch regeneration, suppressed progress, and end-of-batch result/checkpoint writes.
- [Published driver's local connection implementation](https://github.com/tursodatabase/turso/blob/607bff9fd0e547a25a97b3bf06e83bcc5d99d53c/bindings/dotnet/src/Turso.Data/TursoConnection.cs).

## Implemented fixes and validation

The adapter now supports explicit catalog-operation scopes behind the catalog port. Single imports, folder imports and harness recovery keep one serialized native connection for the operation; nested/concurrent constituent calls share it. The service owns a real vault-key lease until the connection closes. Calls outside a scope remain transient. Failure invalidates the cached handle, and snapshots checkpoint/close before copying and reopen on the next call. No process-global keyed connection was introduced.

Automatic WAL maintenance checks the physical WAL at safe operation boundaries and attempts `wal_checkpoint(TRUNCATE)` at 64 MiB, including reopening an older WAL. The limit permits one complete transaction of overshoot. A failed automatic attempt records a counter and defers retries for 30 seconds; it cannot change a successful commit into an import failure and trigger media cleanup. Snapshot/publication checkpoints remain mandatory. FULL durability and source/media verification remain enabled.

Harness progress now prints count-only phase heartbeats every five seconds. Build rows contain native opens/duration, gate waits, observed WAL sizes, checkpoint count/duration/failures, generation/recovery times and source reuse counts. Completed-batch checkpoints retain their original meaning. A pending batch still retains its original size when `--batch-files` changes.

Recovery projects only catalog source identities, paths, hashes and sizes for Turso. Already committed media is verified without recreating deleted sources; remaining committed sources must match the recorded SHA-256 before deletion. Workspace generation receipts permit reuse of uncommitted staging files only after size/hash validation. Legacy uncommitted sources without receipts regenerate once. Receipts are tied to the saved seed range/distributions and removed after a completed batch.

A disposable Release build used the supplied seed, size/metadata distributions, four workers, catalog batches of 16 and four generation threads, with 256 sources per harness batch. It reached 1,024 images with fresh-process warm open measurements at 256, 512 and 1,024. The full test suite ran concurrently, so these timings are correctness/scaling checks rather than isolated benchmark numbers:

| Completed images | Native opens during batch | Ingestion wall time for 256 images | WAL after batch |
|---:|---:|---:|---:|
| 256 | 1 | 4.25 s | 4,659,752 bytes |
| 512 | 1 | 2.60 s | 2,575,032 bytes |
| 768 | 1 | 2.44 s | 2,640,952 bytes |
| 1,024 | 1 | 2.32 s | 2,476,152 bytes |

The native engine's own checkpoint behavior kept this run below the adapter's threshold; no explicit maintenance checkpoint was required. Separate regression tests use a smaller threshold to exercise truncation after a write and on reopening an existing oversized WAL. Tests also cover rollback with a retained connection, wrong keys, snapshots, and draining connections before vault close and catalog relocation.

A separate CLI build was interrupted after 64 commits in a 300-item pending batch. It exited with code 130 and preserved `Pending=300`. Resuming with `--batch-files 50` completed that same pending batch: 64 committed items verified, 236 staging files reused, zero files regenerated, one native connection opened during recovery/import, and the final catalog/checkpoint reached 300. Integration tests additionally check legacy unreceipted regeneration and refusal to delete a modified committed source for both relevant paths.

Final Release test suite: **843 passed, 5 skipped (848 total)**. `git diff --check` also passed.

These implementation checks did not establish throughput at 100k/200k. The original partially built gallery was not opened, checkpointed, regenerated, or otherwise modified while implementing or validating the fixes. The rebuilt Release harness could resume it with the original command. Its first maintenance checkpoint could still take time because of the existing multi-gigabyte WAL; phase heartbeats distinguish that work from a silent stall.

The subsequent user-run stress test completed 200,000 items. See the [completed results and SQLite comparison](2026-10-31-turso-results.md) for the larger validation, scope and limitations.
