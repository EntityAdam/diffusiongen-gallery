# Large-gallery stress and performance test: handoff

Status: plan only. No code has been written. Code references are to `main` at `5671143`, before PR #18.
PR #18 is now merged (per the user) and renames some classes, for example `LocalBackupTarget` becomes
`BackupTargetLocal`. Pull `main` and re-check the names and line numbers before implementing.

**Revision 2 (user decisions).** Targets are **media counts**, not media bytes. The count suite uses
**~1 MB files**. Large-file cost (decryption, thumbnail generation, memory) is tested by a separate, small
**large-image suite**. See doc 02, sections 3 and 7.

## Goal

Build a **separate project** that:

1. **Count suite:** generates synthetic PNGs of about **1 MB** each, with realistic dimensions and ComfyUI metadata.
2. **Ingests** them through the real ingestion pipeline, collecting the existing `IngestionTimingReport`.
3. Measures **vault open/unlock** at several gallery sizes. Vault-open telemetry does not exist yet; the plan
   adds it.
4. Targets **100k, 200k and 400k media items** first. At the user's real average of ~2.4 MB per file, these counts
   equal about **250 GB, 500 GB and 1 TB** of real media.
5. **Large-image suite:** 50 images at each of 5 size tiers, from 4 MB up to the 48 MB / 40 M-pixel import ceiling.
   It measures decryption, thumbnail generation, ingestion memory and viewer load per tier.
6. Gives evidence about whether **5 TB+** (about **2.1M items** at 2.4 MB) is feasible, without needing 5 TB of disk (see doc 05).

Motivating baseline (reported by the user, not measured by us): about 28,000 files, 67 GB of media and a
5 GB `gallery.db`. A fresh, uncached unlock takes about **3 minutes**. Cutting that time is the main goal.
Finding other hot paths is the second goal.

## Documents

| # | File | Contents |
|---|------|----------|
| 01 | [01-context-and-findings.md](01-context-and-findings.md) | What unlock does today (facts with code refs), then hypotheses (clearly separated) |
| 02 | [02-harness-design.md](02-harness-design.md) | Project layout, CLI, PNG generator, ingestion loop, open measurement, cold cache, result schema |
| 03 | [03-vault-open-telemetry.md](03-vault-open-telemetry.md) | Telemetry to add to `Gallery.Core` for the unlock/open path |
| 04 | [04-implementation-plan.md](04-implementation-plan.md) | Phased work items with file ownership, acceptance criteria and subagent boundaries |
| 05 | [05-scaling-beyond-test-capacity.md](05-scaling-beyond-test-capacity.md) | How to say something credible about 5 TB+: catalog simulation and extrapolation |
| 06 | [06-decisions-and-open-questions.md](06-decisions-and-open-questions.md) | Assumptions made in autopilot, and questions to confirm |

## The short version

- The UI's "unlock" is much more than key derivation. It derives the key, then **reads and decrypts every
  catalog record** (`SELECT id, record FROM images`). Then it does several settings reads, each on a **new,
  unpooled SQLite connection that re-runs the DbUp migration check**, and then thumbnail reads. See doc 01.
- The 3-minute cost probably scales with **catalog row count and catalog bytes**, not with media terabytes. This is
  a hypothesis, to be confirmed by telemetry. So the harness must report per-file and per-catalog-byte numbers,
  not only per-TB numbers.
- An extrapolation from the user's numbers (inference, not measured): about 180 KB of catalog per media item. At
  400k items (≈1 TB real) that is a **~72 GB catalog**. At 2.1M items (≈5 TB real) it is a **~380 GB catalog**,
  plus a fully in-memory `List<ImageRecord>` on every unlock. Reaching 5 TB likely
  needs architectural changes (projection or paging), not only tuning. The harness is designed to prove or
  disprove this.

## Suggested order

Phase 0 (profile the real catalog, read-only) → Phase 1 (unlock telemetry) → Phase 2 (harness skeleton and generator)
→ Phase 3 (ingestion build loop) → Phase 4 (open measurement) → Phase 5 (reports) → Phase 6 (5 TB catalog
simulation). Phases 1 and 2 can run in parallel. See doc 04.
