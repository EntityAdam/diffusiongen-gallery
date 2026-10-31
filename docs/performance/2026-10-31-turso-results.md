# Turso 200k stress results and SQLite comparison

Date: 2026-10-31. Summary of the completed Turso count-suite's `state.json`,
`results/ingestion.jsonl`, `results/open.jsonl`, and generated report. Only numeric
results are recorded here; galleries, credentials, and raw local workspace files
are not part of the repository.

## Run and completion

The local macOS/APFS run used .NET 10.0.5, server GC, 15 reported CPUs, seed 42,
four import workers, catalog commits of 16, four generation threads, 50,000
sources per harness batch, `fixed:1MB` images and `uniform:8KB-60KB` metadata.
It reached 200,000 catalog items, four completed batches and `Pending=0`.
Opening measurements used three fresh-process warm samples at each target.

The resumed run recovered 13,504 previously committed items and imported another
186,496, with zero reported import failures. Its recorded wall-clock span was
approximately 55 minutes: 23.0 minutes generating, 30.5 minutes importing, 39.6
seconds reconciling committed media, plus startup and measurement overhead.

| Batch ending at | Newly imported | Imports/s | Import seconds | Generation seconds | Catalog + WAL GB |
|---:|---:|---:|---:|---:|---:|
| 50,000 | 36,496 | 101.66 | 359.01 | 271.90 | 7.117 |
| 100,000 | 50,000 | 102.83 | 486.24 | 371.35 | 14.228 |
| 150,000 | 50,000 | 101.80 | 491.14 | 367.77 | 21.353 |
| 200,000 | 50,000 | 101.36 | 493.28 | 369.56 | 28.475 |

GB are decimal. The first batch includes recovered items, so its import time
covers only the 36,496 new imports. Throughput fell 1.43% between the first and
last complete 50k batches. Each complete batch took about 14.3 minutes including
generation. Cumulative source-media size was 210.91 GB.

## Connection lifetime and WAL

Each batch opened one native Turso connection, with measured native open times
of 0.47, 2.07, 2.05 and 2.35 ms. Maximum observed WAL sizes were 5.92, 5.90, 7.68
and 7.93 MiB. The engine kept the WAL below the adapter's 64 MiB threshold, so
these batch metrics contain no explicit adapter checkpoint events. Initial
startup maintenance is outside the per-batch metrics.

At 200k, catalog commits averaged 40.54 ms per 16 items and accounted for 25.7%
of import wall time. Decode averaged 10.62 ms per item, thumbnail creation 5.05
ms and verification 2.61 ms. Worker stages overlap. `CatalogInsert` repeats a
batch's elapsed commit time for each member, so its aggregate must not be added
to `CatalogCommit` or interpreted as additional exclusive database time.

## Warm first-page opening

| Median measurement | 100k | 200k |
|---|---:|---:|
| Passphrase submission to gallery readiness | 156.52 ms | 206.38 ms |
| Load initial page | 59.11 ms | 106.55 ms |
| First 12 thumbnails | 23.75 ms | 24.51 ms |
| Opening through thumbnail completion | 185.45 ms | 235.76 ms |
| Peak process resident memory | 101.98 MiB | 101.81 MiB |
| Cumulative managed allocations | 9.34 MiB | 9.01 MiB |

All samples loaded 12 actual records and reported an incomplete catalog at open.
No full-catalog or drive-idle wait was enabled. Browser rendering is excluded.
The extra opening time appears primarily in the initial-page load step, which
also queries the total record count. The measurements do not isolate that query
as the cause. With only two sizes, extrapolation to millions is unvalidated.

## Comparison with recorded SQLite results

The [SQLite analysis](2026-10-30-vault-open-analysis.md) records ingestion near
33–35 files/s. Turso's 101–103 files/s is about a threefold observed improvement.
These are different builds; legacy effective GC mode was not recorded, and this
does not isolate a database-engine-only effect.

Preserved historical SQLite warm-open samples provide the catalog sizes below;
the SQLite values are medians over three samples at each count. These older rows
have no backend label and are identified from the documented SQLite baseline.

| Catalog + WAL | SQLite | Turso | Turso difference |
|---|---:|---:|---:|
| 100k | 14.139 GB | 14.228 GB | +0.635% |
| 200k | 28.301 GB | 28.475 GB | +0.616% |

At 200k this is approximately 138.19 KiB/item for SQLite and 139.04 KiB/item for
Turso. The experiment has not shown a storage reduction.

The comparable opening behavior is the optimized SQLite encrypted first-page
cache: three warm server-GC runs at 350k measured 183 ms readiness, 91.7 MiB peak
resident memory and 12 loaded records. Turso measured 206 ms and 101.8 MiB at
200k, also with 12 records. The counts differ, so these results establish similar
initial-opening behavior rather than a winner.

SQLite's historical 5.8-second/23.9-second opens at 100k/200k loaded every record.
They must not be presented as an equivalent backend comparison with Turso's
first-page opening. Comparable Turso full-catalog latency and memory, cold-cache
results and browser rendering measurements are still absent. The remaining
full-record UI loading/search path has not been replaced by this adapter.
