# DiffusionGen Gallery

A local-only application for organizing an encrypted image and video library.
Browse, rate, tag, review duplicates, and optionally describe images with your own
vision model. No model is bundled.

The web interface runs on your computer. Media may be stored on local drives or
NAS/SMB shares, but new catalogs must be on a local drive. This is a single-owner
application, not a remotely accessible gallery service.

## Getting started

Install the .NET 10 SDK. From the repository root, run:

```powershell
dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj
```

Open **http://127.0.0.1:5189**.

1. Choose **Create** from **Switch gallery**.
2. Choose a gallery name and a new local catalog directory. An automatically
   generated name and directory are provided.
3. Optionally enter a separate encrypted media directory, including a NAS/SMB path.
   Leave it blank to store media beside the catalog.
4. Choose a unique passphrase of at least 14 characters.
5. **Save the 32 recovery words offline before continuing.** They are shown only
   once. Losing both the passphrase and recovery words makes the gallery
   unrecoverable.
6. Open the gallery, then use **Ingest images** to import media.

The catalog contains the database and encryption header. The app remembers the
last successfully opened gallery; use **Switch gallery** to open, create, or
restore another one. Default galleries are beneath
`%LOCALAPPDATA%\DiffusionGenGallery\galleries` on Windows.

All paths are entered manually and refer to the computer running the application,
not to a separate browser device. Use fully qualified paths, such as
`D:\Pictures` or `\\server\share\media`. New catalog, restore, and whole-gallery
move destinations must be local; UNC and mapped network drives are rejected.
Existing network catalogs can be opened for migration, but local catalogs are
recommended.

To override the initial gallery directory:

```powershell
$env:Gallery__DataDirectory = 'D:\PrivateGallery'
dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj
```

Do not expose the application through a proxy, tunnel, or public port. An unlocked
gallery has an exclusive checkout: another session or process cannot unlock that
gallery until it is released. Different galleries can run on different loopback
ports.

## Importing media

1. Open **Ingest images**, enter a source folder, and select **Scan**.
2. Choose whether to include subfolders, images, and videos.
3. Check the selected count, available disk space, and estimated size change.
4. Leave **Delete original media** off to keep the source files.
5. Select **Ingest**. Review the final counts and per-file issues.

Changing the source or recursion setting requires another scan. Newly added files
also require rescanning. If a selected file changes or disappears, ingestion stops
and requires a new scan. Do not edit, rename, or replace source files during import.
Browser uploads and drag-and-drop imports are not supported.

| Media | Requirements and limits |
|---|---|
| PNG, JPEG, WebP | Up to 50 MiB, 40 megapixels, and 16384 pixels per dimension. Animated images are rejected. |
| MP4 video | Up to 256 MiB and 16384 pixels per dimension. Both ffmpeg and ffprobe are required; browser playback also needs a compatible codec, such as H.264. |

The **Supported Media** and **3rd party** sections show support and video-tool
availability. The app finds ffmpeg/ffprobe using `Gallery:FFmpegPath` (a folder or
executable), then PATH, then Windows WinGet links.

New imports preserve the original media bytes without re-encoding. Images use
encrypted `.dpng` containers regardless of image format; videos use `.dmp4`.
The encrypted catalog retains the original filename, full source path, containing
folder, and source creation date when available. Earlier records may not have a
creation date, and older converted imports retain their stored format.
Exact source-byte duplicates are reported rather than imported again.

**Delete original media** permanently deletes each source only after the encrypted
import is committed and verified. Deletion failures are reported even when import
succeeds. This bypasses the Recycle Bin and is not secure erasure.
Video import briefly uses a plaintext temporary file; see
[Privacy and recovery](#privacy-and-recovery).

The size change is an estimate, not a guarantee. A separate, more conservative
free-space check runs before import and does not count future original deletion
as available space. Successful earlier batches remain imported if a later batch
fails.

Under **Advanced**, select HDD, SSD, or Custom performance settings and save them
per gallery. Custom supports 1-32 workers and batches of 1-64 files. More workers
use more memory. Optional performance information reports timings without file
or folder names.

## Browsing and organizing

The sidebar contains **Gallery**, **Duplicate finder**, **Ingest images**,
**Settings**, and a **Toolbox** toggle. Collapse it to an icon rail when you need
more space.

### Gallery toolbox

The floating toolbox contains:

| Tool | Purpose |
|---|---|
| Change view | Card size, grouping, sorting, full-screen viewer, and toolbox-position reset. |
| Filter images | Text, model, organization, media/dimensions, vision, and import-date filters. Changes apply immediately; **Reset filters** clears them. |
| Review images one by one | Guided review of pending items. |
| Organize media | Add collections/tags and remove tags from selected items. |
| Search | Ask the configured LLM to suggest explicit filters. |
| Use AI to describe and tag your images | Generate suggestions for approval. |
| Trash | Restore marked items or permanently remove them. |

Click a tool to expand its panel; click it again to collapse it. Toggle the toolbox
with the sidebar button or the backtick key. Drag its grip to reposition it, or
focus the grip and use arrow keys. Double-click the grip, press Shift+backtick, or
choose **Reset toolbox position** to return it to the top-left. Shift+backtick also
shows a hidden toolbox. Position, visibility, and active tool are saved per gallery.

### Compact and Detail cards

Choose **Thumbnail size** in Change view:

- **Compact:** 49 cards per page, no filenames or metadata; rating stars sit above
  the favorite and trash-can icons.
- **Detail:** 12 cards per page, with uncropped previews, filename and status icons
  on the first line, then dimensions, approximate megapixels or video duration,
  and original size in MiB. Model names and a two-line positive prompt appear when
  ComfyUI metadata provides them. Stars, heart, and trash-can icons share the
  bottom footer row.

Detail is the default. Existing Comfortable/Large preferences load as Detail.
Megapixels round to the nearest whole MP, with a minimum of 1MP. Video duration
uses `hh:mm:ss`.

Detail-card indicators have tooltips on hover or keyboard focus:

| Indicator | Meaning |
|---|---|
| `image` / `video` | Media type; always shown in muted green. |
| `eye` | Saved vision analysis; muted green. |
| `eye-dashed` | An AI suggestion awaits acceptance, including when older saved analysis exists. |
| `eye-off` | No saved vision analysis or pending suggestion. |
| `circle-check` | Reviewed; muted green. |
| `circle-dashed-check` | Not reviewed. |
| `sparkles` | ComfyUI generation metadata detected; muted green. Absent metadata does not prove the item was not generated. |

**Vision status is currently inferred:** saved vision notes or an analysis model
produce the `eye` icon. It is not a persisted record of clicking **Approve & save**.
General review status is separate from vision status.

Click a star to set a rating; click the current rating to clear it. Setting a
nonzero rating or marking an item for deletion also marks it reviewed. Clearing a
rating or unmarking deletion does not reset review status. Trashing only marks
media; it does not delete files.

### Selection, filters, and organization

Use the select button on a card, including in Trash. Shift+click selects a range
across pages in the current sort order; Escape clears selection.

The **Organize** tool lists selected items' tags as removable chips. A count means
only that many selected items have the tag. A chip's **x** removes that tag from
all selected items that carry it. Collection and tag **Apply** actions add/merge
values; **Clear** only resets the input. Collections are virtual and never move
files. AI organization suggestions support up to 24 selected images and apply
only the requested collection or tags.

Filters include collections, tags, ratings, review status, favorite/deletion state,
source/current folders, text/metadata, vision and diffusion models, media type,
dimensions, orientation, analysis status, and import dates. Text search includes
user descriptions. **Group by same prompt** groups exact positive-prompt matches;
**Group by diffusion model** groups embedded model names. Prompt groups can be
filtered with **Show only these**. Model-name filtering is partial and
case-insensitive.

### Image details

Click a thumbnail to open details. The preview has a download icon at the top
right and a full-screen icon at the bottom right. Rating, favorite, and trash
controls sit below it.

Desktop uses **File**, **Generation** (when detected), and **Notes** tabs; mobile
stacks the sections. The preview scales proportionally within the desktop dialog.

- **File:** original filename, dimensions, size, video duration, source creation
  date, import date, and original source path. Missing source creation dates show
  **Unknown (not captured)**, not the import date. Filesystem creation dates are
  not necessarily generation dates.
- **Generation:** ComfyUI model filename without `.safetensors`, steps, CFG,
  sampler/scheduler, positive prompt, and negative prompt when present.
  **Download workflow** exports the workflow or API prompt. UI-only workflows may
  be detected without providing parsed fields. Other tools' metadata and raw JSON
  are not displayed.
- **Notes:** separate **Description** (your notes) and **Vision notes** textboxes.
  **Save notes** saves both. **Analyze locally** immediately saves the returned
  vision notes, replaces tags, records the model, and saves the current user
  description; it does not wait for the suggestion-approval workflow.

Manage collections/tags through Organize, not this dialog. Current encrypted paths
and move/rename controls are not exposed here.

### Keyboard shortcuts and full-screen viewing

On a focused gallery card, or the card under the pointer:

| Key | Action |
|---|---|
| 1-5 / 0 | Set / clear rating. |
| F | Toggle favorite. |
| D | Toggle deletion mark. |
| X / Shift+X | Toggle selection / select a range. |
| Escape | Clear selection. |
| Arrow keys | Move card focus. |

These shortcuts are ignored while typing or while a dialog/viewer is open.

**Full screen view** in Change view opens the current page; the details preview
also provides access. Left/Right navigate within that page, D toggles deletion,
F toggles favorite, O shows original size, P fits to the window, and Escape closes.
Changes save immediately, but the viewing sequence stays fixed until closing.
Fit mode uses viewing previews; original-size mode loads the stored original.
Video playback depends on the browser's codec support.

### Guided review

In the Review tool, choose a **Goal** (default 10) and **Start Review**. It selects
pending, unmarked items from the current filtered results across all pages,
limited to the number available. **Random order** samples without duplicates;
otherwise the current sort order applies. The sequence stays fixed for the session.

| Key | Review decision |
|---|---|
| K | Mark reviewed and clear deletion; preserve rating/favorite. |
| F | Mark reviewed, favorite, and clear deletion. |
| D | Mark reviewed and mark for deletion. |
| 1-5 | Rate, mark reviewed, and clear deletion. |
| S / Skip | Advance without changing the item. |
| Arrows | Browse without a decision. |

Decisions save immediately and advance. **Undo** restores the previous review
state, rating, favorite, and deletion mark; skips are not undo entries.
Completion requires a decision or skip for every session item. Skipped items
remain pending in the library; browsing past an item does not count as reviewing
it. Closing early retains saved decisions.

### Duplicate finder

Scan for likely duplicates, including resized/re-encoded images. Results are
recommendations, not proof that files are interchangeable. A keeper is recommended
using review-related preferences such as rating, favorite, resolution, and size.
Marking extra copies is reversible; remove them permanently through Trash.

**Review in full screen** or **Review & choose keepers** lets you keep one or more
copies. **Apply** unmarks kept copies and marks the rest, then advances. **Skip
group** changes nothing. Shortcuts: Left/Right copies, Up/Down groups, K toggle
keep, A apply, S skip, O original size, P fit, Escape close.

## AI analysis and search

Under **Settings > LLM endpoints**, configure an OpenAI-compatible endpoint,
optional bearer API key, loaded vision-capable model, and timeout (10-3600 seconds,
default 180). **Fetch models** checks the connection without saving settings; model
discovery does not verify vision capability. Save the settings before analysis.

Examples: LM Studio `http://127.0.0.1:1234/v1/`, Ollama
`http://127.0.0.1:11434/v1/`. Use a literal loopback or private LAN IP:
`127.0.0.1`, `::1`, `10.x.x.x`, `172.16.x.x` through `172.31.x.x`, or
`192.168.x.x`. Hostnames (including `localhost`), public addresses, other IPv6
addresses, redirects, and proxies are not supported.

**Privacy:** analysis sends decrypted, metadata-free previews and relevant catalog
text to that server. A private LAN endpoint is still another machine: trust its
operator and use HTTPS when available. The gallery itself remains loopback-only.

In the AI toolbox panel:

- **Play** analyzes images without vision descriptions or pending suggestions.
- **Analyze selected** analyzes up to 24 selected images.
- The eye-dashed badge opens a pending suggestion in the full-screen viewer.
  **Approve & save** replaces vision notes, merges tags, and adds the suggested
  collection. **Dismiss** discards it. Locking also discards pending suggestions.
- **Stop** finishes the current request before stopping; the configured timeout
  still applies. Per-image failures are reported.

Analysis runs only when requested and does not change rating, review state,
deletion marks, or media files. The details dialog's **Analyze locally** action
saves directly, as described above; it is not the same approval workflow.

The navigation job indicator remains visible across workflows. While an LLM job
runs, browsing/review remain available, but file operations and gallery
configuration changes are blocked. Stop and wait for it to finish first.

The **Search** tool converts natural language into explicit filters. Review and
apply the suggested filters; this is not semantic/vector search.

## Trash and permanent removal

The trash-can button only sets a reversible deletion mark. In Trash, **Restore**
clears that mark; it does not restore a backup or a deleted source file.

To delete permanently, select marked cards and choose **Delete Forever**, or
choose **Remove this image** in a marked item's File details. Review the removal
plan and explicitly confirm:

- If the original source is accessible, retain it or delete it too. Source deletion
  verifies the source hash and refuses changed files.
- If the source is missing, restore the stored copy to an existing folder first,
  or discard it without restoration. Restored files use the item ID and stored
  format's extension, without overwriting existing files. Older converted imports
  restore their stored format, not their pre-conversion source bytes.

Permanent removal deletes catalog information and queues encrypted-file deletion.
Bulk removal reports individual failures and continues. Background operations
may finish later; cancellation can leave encrypted files on disk.
Removal bypasses the Recycle Bin and is not secure erasure. Restored copies are
plaintext. Backups and other copies are not deleted.

## Settings, storage, and backups

### Overview and background work

**Settings > Overview** shows cached database/file sizes and counts, the last
measurement time, and duplicate-scan information. Select **Refresh** to measure;
changes can mark cached totals **Stale data**. Missing files are reported and
excluded from totals. **Rebuild file index** detects media-folder changes made
outside the application.

The sidebar shows background file work with **Pause**, **Resume**, **Retry
failed**, and **Cancel**. Closing or pausing does not discard the queue. Cancel
discards queued work and leaves affected files on disk. Locking can leave active
file work running; checkout is released after operations drain.

### Manage gallery

Under **Settings > Manage gallery**:

- Rename the gallery without changing its directory.
- **Save media location** changes the destination of future imports only.
- **Relocate catalog only** moves the database/header to a new/empty local
  directory, preserving existing encrypted media paths and the future-import
  location. Sign in at the new catalog location afterward.
- A whole-gallery move consolidates catalogued media into a new/empty local
  directory. Restore also consolidates media. Both reset the future-import
  location and require signing in afterward.
- Purge requires typing **PURGE**. It permanently removes gallery data and
  catalogued encrypted media, not original sources, backups, or unrelated files.

Use these workflows rather than moving a database manually: stored media paths
are absolute. A copied database can still reference the original gallery's files.
Use a complete separate backup for testing.

### Back up

**Settings > Backups** supports a local/SMB destination or an existing Azure Blob
container. SMB uses the application's Windows account; Azure uses a saved,
encrypted Storage Account connection string. Use a dedicated account.

- A **full backup** contains the header, consistent catalog snapshot, thumbnails,
  and all catalogued encrypted media, including media outside the catalog folder.
- An **incremental backup** contains a fresh catalog/header snapshot and only new
  or changed encrypted media. If nothing changed, no archive is created.
- Keep the full archive, every required increment, and their `.manifest`
  companions together. A new full backup starts a separate chain.

Backups are manual, are not automatically pruned, and cannot be placed inside the
gallery. Move/restore resets backup-chain tracking; create a new full backup
before another incremental.

### Restore a backup

Use **Switch gallery > Restore** with a local/SMB backup folder or an Azure
container SAS URL with read/list permissions. Select **Find backups**, choose a
full archive or the last desired incremental, and provide the backup gallery's
passphrase or recovery words, a name, and a new/empty local destination.

Every archive back to the full backup is required. Companion manifests are not
required for restoration because the archives include them. Restore verifies
content before publication, rewrites media paths, and does not overwrite the
existing gallery or backups. Sign in normally afterward.

## Privacy and recovery

- **Encryption at rest:** media, catalog record/thumbnail blobs, settings, and
  saved credentials are encrypted with authenticated AES-256-GCM. The header
  stores wrapped keys, not the passphrase or plaintext master key. SQLite and
  the filesystem still reveal information such as opaque IDs, sizes, timestamps,
  and encrypted-file locations. Gallery names and the last-used catalog location
  are not secret.
- **Recovery:** the 32 words are application-specific, not a BIP39 wallet seed.
  Use them to unlock, then change the passphrase in **Gallery security**. They
  remain valid and cannot be displayed again. There is no password reset or
  bypass. Losing both credentials, or losing/damaging the header without a
  backup, can make content unrecoverable.
- **Backups:** passphrase changes affect the current header, not older backups.
  Old backups can still use their old passphrase.
- **Locking:** configure inactivity locking under **Gallery security** (default
  15 minutes). Panic shortcuts are Ctrl+Shift+L, Alt+Shift+L, and Shift+Escape.
  Lock clears the active viewing session and decrypted media URLs. In-flight
  operations can retain keys until they finish; checkout releases afterward.
  Locking is not a guarantee that all operating-system/browser memory is erased.
- **Plaintext exposure:** viewing/analysis uses server, browser, and model-server
  memory. Malware, extensions, screenshots, swap, and process dumps are outside
  the at-rest guarantee. Originals remain plaintext unless separately deleted.
  Video import briefly writes `%TEMP%\gallery-<guid>.mp4` and deletes it afterward,
  without secure erasure.
- **Exports:** download icons create plaintext copies. Export cleaning preserves
  standard metadata, including
  supported EXIF/XMP and ComfyUI information; it is not metadata anonymization.
  It does not re-encode pixels or transcode videos. Protect exported files and
  workflows accordingly.
- **Deletion:** source deletion, permanent removal, and purge are not secure
  shredding. They do not remove backups, snapshots, synced copies, or old SSD
  flash pages. Use full-volume encryption before writing sensitive plaintext.
- **Network:** keep the gallery loopback-only. Only the optional configured
  model endpoint and storage/backup services should involve another machine.

## Developer notes

There are two production projects: `Gallery.Web` references `Gallery.Core`.
Core currently contains models, domain rules, application services, SQLite,
encryption, filesystem/media processing, and external gateways. Web contains
Blazor components, host/request policies, and dependency-injection composition.
UI components use Core models directly. Test projects are under `src/tests`.

`GalleryServiceRegistration` captures runtime settings once through
`GalleryService.CaptureRuntimeOptions`. Core's `ConfigurationProvider` and
`SettingsSource` remain internal. `Gallery:StateDirectory` selects
the last-opened-gallery history directory. Tool search paths retain their order:
configured `PATH` (or process `PATH` when absent), followed by user and machine
`PATH` on Windows. The snapshot keeps raw tool paths; `GalleryService.Create`
normalizes them once when constructing each scope. Restart the host to apply
runtime-setting changes.

Browsing projections require immutable image and index collections. Invalid
saved-view names throw `InvalidSavedViewNameException` before vault access,
with the normalized name and permitted length range in diagnostic context.
The workbench preserves the existing caller-facing validation message.

The UI uses ShadCn.Blazor, Lucide, and Tailwind. Generated CSS is checked in, so
Node is not needed just to run the app. After changing Razor utility classes,
run from `src/Gallery.Web`:

```powershell
npm ci
npm run build:css
```

`npm run watch:css` supports active styling work. Theme and gallery-specific
rules live in `wwwroot/app.css`.

ImageSharp 4 requires a valid Six Labors license for Release builds. Obtain a
license from [Six Labors](https://sixlabors.com/pricing/) and place it in
`sixlabors.lic` at the repository root, or pass
`-p:SixLaborsLicenseFile=<absolute-path>`. The Core project uses that path for
Debug and Release. The root license file is not excluded by `.gitignore`;
do not commit private license material.

```powershell
dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj --configuration Release
dotnet test .\src\Gallery.slnx
node --test .\src\tests\viewer-drag.test.cjs .\src\tests\viewer-keys.test.cjs
```

Retained but unexposed code includes saved filter views, two-image comparison,
selection ZIP export, and additional bulk organization/review operations. These are not user-facing
features. Explicit persisted vision-acceptance tracking is a future change;
the current card icon is inferred from saved analysis and pending suggestions.
