# 06: Decisions and open questions

These are the assumptions made so the plan could be finished without blocking. Each has a default. Change any of them before implementation starts.

| # | Question | Default used in this plan | Why it matters |
|---|----------|---------------------------|----------------|
| Q1 | Size targets: bytes or item counts? | **Resolved (user).** Targets are item counts (100k, 200k, 400k ≈ 250 GB, 500 GB, 1 TB of real media at 2.4 MB per file), using ~1 MB files. Large files are covered by the large-image suite: 5 tiers from 4 MB to 48 MB at the 40 M px import ceiling, 50 images each (doc 02, section 7). | Unlock cost scales with item count. Large-file cost is per item, and is measured separately. |
| Q2 | Catalog and media on the same drive? | **Same drive** by default. `--media-directory` is supported. | Cold-cache I/O mixes on one disk. Match the real setup. |
| Q3 | One growing vault (100k → 200k → 400k) or three separate vaults? | **One growing vault.** | Saves about 300 GB and many hours. The catalog grows the way a real one does. Fragmentation is realistic, though not identical to separate builds. |
| Q4 | Include videos? | **No.** PNG only. | The ingestion path for video differs. Add later if the real gallery has many videos. |
| Q5 | Cold-cache method | **Purge the standby list** (needs an elevated shell). Fallback: dismount and remount a VHDX, or reboot between runs. | Without admin rights, "cold" runs are actually warm. They are marked `cachePurge: "failed"`. |
| Q6 | Measure UI render time (Blazor or Playwright)? | **Deferred.** The harness mirrors the `Home.razor` sequence in-process. | Misses render cost (H7). It can be added after P7 moves the sequence into Core. |
| Q7 | Defender or antivirus exclusions for the stress folders? | **Recommend, don't require.** Record the status in the environment block. | Real-time scanning of 1 TB of new files distorts ingestion numbers heavily. |
| Q8 | Where does the harness code live? | `src/perf/Gallery.Stress` and `Gallery.Stress.Core`, added to `Gallery.slnx`, on its own branch in a **worktree**, committed and PR'd as **EntityAdam** once you approve. | Standing instructions. |
| Q9 | Should Phase 1 (telemetry) wait for PR #18? | **Resolved (user): #18 is merged.** Pull `main` and branch from it. | The line numbers in these docs are from before the merge. |
| Q10 | Is the ~3-minute unlock measured to the first thumbnails painted, or to the unlock spinner closing? | Assumed **to the first page of thumbnails visible**. | This decides whether the thumbnail reads and storage overview steps count toward the target. |
| Q11 | Is it acceptable to change the DB schema (split thumbnails or details) if the data supports it? | Assumed **yes, through a DbUp migration**, but only after P5. | The biggest likely wins (P7) need a one-time migration over the whole catalog, which is itself slow on large vaults. |
| Q12 | Passphrase and KDF for stress vaults | Production KDF settings (600k iterations), with a generated passphrase stored in `stress-vault.json` next to the vault. | The KDF time is constant (about 0.5–1 s) and reported separately. The stress vaults are disposable; never reuse that passphrase. |

## Facts vs. assumptions

- **Facts** (from the code, in doc 01, Part A): the unlock sequence, the full-catalog read, KDF settings, migration on every connection, schema layout,
  and the lack of open telemetry.
- **Inferences** (doc 01, Part B): the ~180 KB per item and the H1–H8 bottleneck ranking. **None has been measured yet.** P0, and P5 run 1 (telemetry on
  the real gallery), exist to confirm or reject them before any optimisation work.
