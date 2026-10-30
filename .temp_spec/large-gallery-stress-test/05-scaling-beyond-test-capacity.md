# 05: Scaling beyond test capacity (5 TB+)

**Fact:** the user cannot store 5 TB. **Inference:** unlock cost is driven by the catalog (row count and record bytes), not by media bytes;
see doc 01, A3–A6. Media files are not read during unlock, apart from the background media index scan, H5. So 5 TB behaviour can be
approximated by building a **realistic catalog** without the real media.

## `simulate-catalog` command

```
Gallery.Stress simulate-catalog --vault <path> --rows 500k,1M,2.1M
    [--record-dist profile.jsonl] [--thumbnail-kb 40-120] [--sparse-media] [--seed N]
```

1. Create a normal vault through the public API (`CreateVaultAsync`), so the header, KDF and schema are real.
2. Write rows directly with internal `GalleryStore` APIs. This requires `InternalsVisibleTo("Gallery.Stress")` in `Gallery.Core.csproj`.
   - Generate an `ImageRecord` with realistic fields. `Metadata` (prompt and workflow) size is drawn from the P0 profile distribution.
   - Encrypt it with the vault key through the same `VaultCrypto` path as production.
   - Use a thumbnail blob of realistic size, either random bytes or a pre-encoded PNG reused with a varied suffix. Thumbnails are not decoded at unlock.
   - Generate a unique fingerprint per row.
   - Batch inserts in transactions of about 5,000 rows.
3. `--sparse-media`: for each row, create the `images/xx/yy/<id>.dpng` file as an **NTFS sparse file** with the right logical length
   (`fsutil sparse setflag` plus `SetEndOfFile`, or `FileStream.SetLength` after `FSCTL_SET_SPARSE`). It uses almost no disk. This lets
   `RefreshStorageOverviewAsync` and the media index scan see realistic file counts and logical sizes.
   - **Caveat:** these files cannot be decrypted. Never open them in the viewer.
4. Write `stress-vault.json` with `"simulated": true`. `measure-open` copies this flag into every result row, and `report` keeps simulated and
   real rows in separate series.
5. Tiers are additive in one vault: 500k, then 1M, then 2M rows. The real builds already cover 100k–400k. Run `measure-open --cache cold,warm --runs 3` at each tier.

### Disk estimate (catalog only)

| Rows | ≈ Real media equivalent (2.4 MB per file) | Catalog size (~180 KB/row, to be confirmed by P0) |
|------|-------------------------|-----------------------------------------|
| 500k | 1.2 TB | about 90 GB |
| 1M | 2.4 TB | about 180 GB |
| 2.1M | **5 TB** | **about 380 GB** |

If P0 shows that records are much smaller, for example 20 KB with thumbnails being most of the bytes, these numbers drop sharply. **Run P0 first.**
If the 2.1M tier does not fit, stop at 1M and extrapolate. The fit then spans 100k–1M, which is a 2× extrapolation to 5 TB.

## Model fit

For each step, fit `t(N) = a + b·N` (the warm and cold series separately), using every tier from the real builds (doc 04, P5) plus the simulated tiers.

- Report `a`, `b`, R², and the predicted time at 1M rows, with a 95% prediction interval.
- Also fit against `record_bytes`. If R² is clearly better against bytes than against rows, cost is bandwidth-bound (H1 and H2). If rows fit better,
  cost is per-row (decrypt, deserialise or allocation).
- **Check that real and simulated data agree:** at nearby row counts (the real 400k build vs. the simulated 500k tier),
  the slopes should agree within about 15%. If they do not, the simulated record shape is wrong. Fix it, using P0, before trusting the extrapolation.

## Known limits of the extrapolation

| Limit | Why it matters |
|-------|----------------|
| **Memory ceiling (H8)** | `LoadAsync` keeps every `ImageRecord`, including the full ComfyUI JSON, in a `List`. At 1M rows × the in-memory size (often 2–3× the JSON bytes), this may exceed RAM, which is a cliff rather than a line. Record `peakWorkingSetBytes` and GC counts per tier. If it grows faster than linearly, or the process fails, report the failure point as the result. |
| SQLite cache and OS file cache | Once the catalog is bigger than RAM, the warm-cache series stops being warm. Expect the warm and cold series to converge above a certain tier. |
| Media index scan (H5) | Sparse files exercise directory enumeration, not reads. That is enough for unlock, because the scan runs in the background, but note it. |
| UI rendering (H7) | Not measured (doc 06, Q6). Blazor diffing or serialising large lists may add time that the harness does not see. |
| Hardware | The results apply to the user's machine and drive. Record the environment in every row. |

## Acceptance

- `simulate-catalog --rows 10k` on a temporary vault produces a vault that `open-once` loads, with `record_count == 10000`. The real app also
  opens it; this is a manual check.
- The sparse media files report the correct logical size, and their on-disk size (`GetCompressedFileSize`) is under 1 MB in total.
- `report` shows a separate "simulated" series and the real-vs-simulated agreement check.
