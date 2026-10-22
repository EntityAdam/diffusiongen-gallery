# DiffusionGen Gallery

A standalone local-only .NET 10 / Blazor Server application for managing private
creative libraries. Its charcoal and mint palette, typography, and DiffusionGen
Gallery branding are implemented within this application.
There are exactly two production projects: `Gallery.Web` depends on
`Gallery.Core`, never the reverse. The existing projects under `src/tests` are
test-only tooling.

## Architecture

`Gallery.Core` is a standalone .NET class library with no project dependencies.
It declares the ImageSharp package reference so the assembly-scoped Six Labors
license is validated against `Gallery.Core`; Web consumes ImageSharp transitively.
Core owns domain records and the rules for curation, review/undo,
duplicate grouping and keeper selection, prompt grouping, gallery filtering,
and removal eligibility/decisions. Its operations work on in-memory domain
values; they do not access files, databases, HTTP, configuration, or UI services.
Collection membership changes and the implicit review state of record edits
also live here. Review undo restores an explicit snapshot without reapplying
the implicit rules for a new curation decision.

`Gallery.Web` is the outer application/adapter layer. It owns Blazor and HTTP
boundaries, application orchestration (`GalleryService`), SQLite and encryption,
filesystem operations, media decoding/hashing and metadata parsing, vision
gateways, diagnostics, and dependency injection. With only two production
projects, these outer-layer roles remain folders within Web rather than
additional assemblies.

Web uses its own `Web`-prefixed records and enums in `Gallery.Web.Models`.
`DomainMappings` explicitly translates those values to/from `Gallery.Core.Models`
when invoking domain rules. Neither UI components nor serialized persistence
contracts expose Core records. Existing serialized property names, settings IDs,
and encryption purposes are preserved, so the model type renaming requires no
catalog migration.

Boundary validation belongs to Web (and to any future adapter), not Core.
Adapters validate incoming values and choices before mapping to the domain;
Core applies business rules to those accepted values. Format parsing errors,
HTTP status codes, logging context, size limits, paths, credentials, and external
service settings remain outside Core. Domain rules should be added to Core,
not reimplemented in components or storage adapters.

Image edit callbacks expose only curation and annotation fields. Web validates
the returned edit (including identity, ratings, initialized collections, and
collection names) before mapping or persistence. Names/paths change through the
move workflow; parsed generation metadata changes through import/backfill,
not user edits. Single and batch updates return the committed records, which
the UI uses directly rather than replaying callbacks against stale snapshots.
Batch updates still attempt every selected image and report aggregate failures.

The gallery caches its browsing projection across renders, pagination, density
changes, and thumbnail updates. Record changes invalidate it; filter/sort and
prompt-grouping changes rebuild it on demand. Locking clears the cached records
and prompt groups along with the rest of the unlocked gallery state.

## UI

The UI uses [ShadCn.Blazor](https://www.shadcn-blazor.dev/) components and
Lucide icons, with the original charcoal/mint palette supplied through Shadcn
theme tokens. Buttons, fields, cards, tabs, badges and alerts share the library's
sizing, focus and disabled states. Typed number/date inputs and enum selectors
retain Blazor's native binding with matching Tailwind utilities.

Application layouts use Tailwind utilities. `wwwroot/utilities.css` is generated
and checked in, so running the gallery requires only .NET. After editing Razor
utility classes, rebuild it from `src/Gallery.Web`:

```powershell
cd .\src\Gallery.Web
npm ci
npm run build:css
```

Use `npm run watch:css` while changing layouts. `wwwroot/app.css` contains the
palette, branding, and gallery-specific media/density behavior; it does not
reimplement the component library's control styling.

## Run

Install the .NET 10 SDK, then from the repository root:

```powershell
dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj
```

ImageSharp 4 requires a valid Six Labors license for Release builds. Apply for
the free Community license at [Six Labors pricing](https://sixlabors.com/pricing/)
if eligible and save the issued license as `sixlabors.lic` in the repository root.
The Core project automatically uses this file for Debug and Release builds,
validating the license against its licensed assembly name, `Gallery.Core`.
To run a Release build in PowerShell:

```powershell
dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj --configuration Release
```

An explicit `-p:SixLaborsLicenseFile=<absolute-path>` overrides the default location.
The repository-root license file is not excluded by `.gitignore`.

Open **http://127.0.0.1:5189**. Create a gallery with a unique passphrase of at least
14 characters and **save the 32 recovery words offline before continuing**.
New galleries get a pseudo-random name and their own folder beneath the OS-specific
local application data directory (`%LOCALAPPDATA%\DiffusionGenGallery\galleries`
on Windows). You can edit the name and directory before creation. The welcome
screen remembers the last successfully opened gallery and offers **Switch gallery**
to create, open, or restore another gallery. Enter a fully qualified local directory
for catalogs, and local or UNC/SMB paths for media and backups (or absolute mounted
SMB paths on Linux/macOS). Local-drive enforcement detects UNC and mapped network
drives on Windows; on Linux/macOS, keep catalog directories on local filesystems
rather than network mounts.
All directory locations use manual path entry; there are no folder-picker controls.
To override the initial directory, set `Gallery__DataDirectory`:

```powershell
$env:Gallery__DataDirectory = 'D:\PrivateGallery'
dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj
```

Gallery names are visible in the header, and the last-used directory is saved in
`last-vault.json` in the application's local data directory. Media and catalog
contents remain encrypted.

The process binds to loopback and rejects non-loopback clients, untrusted Host
headers, and cross-origin requests. Do not expose it through a proxy, tunnel,
or public port. This is a single-owner local application, not a network service.
An unlocked gallery holds an exclusive filesystem checkout. Locking the gallery
automatically releases its checkout after interrupted operations finish. Another
browser session or application instance cannot unlock the same gallery concurrently.
The `.vault-checkout` file is deliberately retained; its presence alone
does not indicate an active checkout. SMB servers must honor exclusive file sharing.
Multiple processes can use different galleries; give each a different loopback port,
for example `dotnet run --project .\src\Gallery.Web\Gallery.Web.csproj -- --urls http://127.0.0.1:5190`.

**Create gallery** separates the **local catalog directory** (`gallery.db` and
`vault.json`) from an optional **encrypted media directory** (local or NAS/SMB).
Leave the media directory blank to store media beside the catalog. Media paths and
storage settings are encrypted in the catalog. New catalogs, whole-gallery moves
and restores require local storage, including rejection of mapped network drives.
Local catalogs use SQLite WAL with `synchronous=FULL`. Existing network catalogs
remain openable in rollback-journal mode for migration; this is not a guarantee
against unreliable SMB locking. Keep only one checked-out process per gallery.

**Settings > Manage gallery** can **Relocate catalog only** to a new/empty local
directory, preserving every existing encrypted media path (including NAS media),
without copying large media files. The database snapshot is checked before publication;
the old header and database are removed only after publication. Sign in at the new
catalog location afterward. The current media directory is preserved for future
imports. **Save media location** changes where future imports go, not existing files.
Backups still include all catalogued media, regardless of its location. Restore and
whole-gallery moves reset the media directory to the new local gallery.

The same screen renames a gallery without changing its directory, moves
the entire gallery to a new/empty local folder, or purges it after typing **PURGE**.
Moving consolidates all catalogued encrypted media (including media previously
moved outside the gallery) into the new directory and updates the encrypted catalog.
The source is removed only after verified copies and a database snapshot are
published. Sign in again after moving. Purge destroys encrypted media, the catalog,
thumbnails, settings, and the gallery header; original source files, backups, and
unrelated files are retained. Purge is permanent deletion, not secure disk erasure.

## Workflow

### Smart library, curation and discovery

**Settings > LLM endpoints** lets you enter the API base URL,
an optional bearer API key and a request timeout (10-3600 seconds, default 180).
**Fetch models** tests the entered connection and populates the model selector
without saving settings. You can also enter a model ID manually. Discovery does
not verify vision capabilities; select a vision model for image analysis.
**Save LLM settings** stores the configuration, including the key, encrypted in
the gallery. Unsaved form edits are isolated from the active configuration; leaving
the endpoint workflow discards them. The same saved connection, key and timeout apply to image analysis, collection
suggestions and natural-language filter interpretation. Literal loopback and RFC 1918
private LAN addresses are allowed: `10.0.0.0/8`, `172.16.0.0/12` (172.16 through
172.31), and `192.168.0.0/16`. Use the server's actual IP, not a network base address.
Hostnames, public addresses, other IPv6 addresses, redirects and proxies remain
blocked. Use `127.0.0.1` rather than `localhost`. A LAN endpoint receives decrypted
previews and metadata; trust the server and use HTTPS when available, since HTTP
does not encrypt traffic in transit. The gallery web application itself still binds
only to loopback.

The workspace uses ShadCn **AppLayout** with a collapsible sidebar and independently
scrollable workflow content. The sidebar toggle switches between labeled navigation
and an icon rail; workflow tooltips and the panic-lock button remain available
when collapsed. There is no app header. A sidebar footer shows DiffusionGen Gallery
branding and the application's semantic build version (without commit metadata);
the icon rail retains the brand icon and version. Primary content views have no
footer; full-screen viewers retain theirs.

The left navigation contains **Gallery**, **Duplicate finder**, **Ingest images**
and **Settings**, plus a **Toolbox** toggle. Gallery opens by default after unlock.**Settings** opens on a **Gallery overview** with tabs for **Backups**,
**Manage gallery**, **LLM endpoints** and **Gallery security**. The overview shows database size (`gallery.db` plus its WAL/SHM
files, which include encrypted thumbnails), total file count (catalogued encrypted
image files plus gallery header/database files) and total size on disk, auto-scaled
from B through PB. Measurements are cached encrypted in the gallery with a "Last
updated" time and are only recalculated when you select **Refresh**, including the
first measurement. Imports, moves, removals, catalog edits and file-index rebuilds
mark existing measurements as **Stale data** beside that timestamp; cached totals
remain visible until you refresh. Catalogued images missing from disk are reported
and excluded from the totals. The overview also summarizes the last duplicate scan.

**Settings > Backups** writes ZIP snapshot archives to one configured destination:
a local folder/SMB share or an Azure Blob Storage container. The app uses its Windows
account for SMB access. Azure requires an existing container and a Storage Account
connection string; that credential is stored encrypted in the gallery and is not shown
again after saving. Use a dedicated Storage Account for backups. A **full backup**
contains `vault.json`, a consistent SQLite database snapshot (including encrypted
thumbnails), an encrypted media manifest, and all catalogued encrypted `.dpng` and
`.dmp4` files, including media moved outside the gallery folder. An **incremental
backup** includes a fresh database/header snapshot and only new or changed encrypted
media. If nothing has changed since the last backup (same media, database content,
and gallery header), no incremental archive is created and the chain is left as is.
Each archive has a companion `.manifest` file; manifests are encrypted and
link each increment to its parent. Keep the full archive, every subsequent incremental
archive in that chain, and their `.manifest` companions together. A new full backup
starts a separate chain. Backups are manual; the app does not schedule runs or prune
older archives. Backup destinations cannot be inside the gallery.

**Switch gallery > Restore** supports a local/SMB backup folder or an Azure Blob
container-level SAS URL with read and list permissions. **Find backups** lists available archives. Choose a full backup
or the last incremental you want restored; restore follows its authenticated parent
links and requires every archive back to the full backup. Companion manifests are
not needed for restoration because archives contain the same encrypted manifests.
Enter the backup gallery's passphrase or recovery words, a gallery name, and a new/empty
destination. Restore verifies the header, database, and encrypted media before
publishing anything, restores the latest catalog state (including deletions), and
rewrites media paths into the chosen directory. The existing gallery and backup files
are never overwritten. Sign in normally after restoration. Move and restore retain
backup credentials/settings but reset backup chain status and cached storage
measurements; create a new full backup before the next incremental.

**Duplicate finder** finds likely duplicates, including resized or
re-encoded copies that import's exact-byte check cannot catch. Each image gets a
64-bit perceptual difference hash computed from its decrypted thumbnail;
images within 6 of 64 bits and with a similar aspect ratio (or identical PNG
pixels) are grouped. Fingerprints and the last scan result are cached encrypted
in the gallery, and rescans only fingerprint new or changed images. Each group shows
thumbnails, sizes and approximate reclaimable space, and recommends a keeper:
unmarked first, then highest rating, favorite, most pixels, largest original and
oldest import. **Mark other(s) for deletion** (per group) or **Mark all extra
copies** only sets reversible deletion marks; permanently remove them from
**Trash**.

**Review in full screen** (or **Review & choose keepers** on a group) opens a
full screen duplicate review. Step through each copy at full size, see its
dimensions, file size, rating and favorite status, and choose **one or more**
copies to keep from the filmstrip or with `K`. **Keep only this** and **Keep all**
are quick picks. **Apply** unmarks the kept copies, marks the rest for deletion
and moves to the next group; **Skip group** leaves a group unchanged. Reopening a
group you partly marked earlier restores those choices. Shortcuts: Left/Right
copies, Up/Down groups, `K` toggle keep, `A` apply, `S` skip, `O` 100%, `P` fit,
`Esc` close.

**Gallery** is a single view: the main content shows only image cards, group
headers and paging controls. All tools live in a floating, movable, compact
**toolbox**. Toggle it from the sidebar or with the backtick key (`` ` ``). The
toolbox starts as a row of icons with tooltips. Activating a tool highlights it and
expands its panel below the icons; only one panel is open at a time, and activating
the same tool again collapses the toolbox back to icons only:

- **Change view** (grid icon): thumbnail size, grouping, sorting, **Full screen view**
  and **Reset toolbox position**.
- **Filter images** (funnel): **Search** and **Diffusion model**, then collapsible
  **Organization**, **Media & dimensions**, **Vision analysis** and **Imported date**
  sections. Each section shows its active-filter count and opens automatically when
  it contains active criteria. Results update as you change a field; **Reset filters**
  clears everything. The tool icon shows the number of active filters.
- **Review images one by one** (circle check): guided review.
- **Organize media** (folder): collections and tags.
- **Search** (magnifying glass): converts natural language into filters.
- **Use AI to describe and tag your images** (sparkles): local intelligence.
- **Trash** (trash can): shows images marked for deletion.

Drag the toolbox by its grip handle (or focus the handle and use the arrow keys).
Its position, visibility and active tool are saved encrypted per gallery. The
toolbox is always kept inside the visible content area: if the window shrinks or
the gallery opens on a smaller display, it is clamped back into view without
overwriting the saved position. To return it to its default place (top-left of the
content area), double-click the grip handle, press ``Shift+` ``, or use **Reset
toolbox position** in the view tool. ``Shift+` `` also shows a hidden toolbox.

Busy, status and error messages appear as toast notifications in the bottom-right
corner.

**Thumbnail size** (Compact, Comfortable, Large) is in the view tool.
Compact shows 49 smaller cards per page, Comfortable shows
24 and Large shows 12. The choice is saved encrypted per gallery and restored on
unlock.

Image details have a **Download a copy** icon over the top-right corner of the
preview and a **Full screen view** icon over the bottom-right corner (above playback
controls for videos). Rating stars, favorite and Trash controls sit below the preview.
Click the current rating again to clear it. Workflow export is no longer a separate
modal action; downloaded copies retain embedded ComfyUI metadata.

Every card, including in Trash, has a select button. Click it to toggle an image,
or Shift+click to select the range from the last selected image (across pages, in
the current sort order). Selections remain across pages until cleared with `Esc`.
There is no Quick Edit panel; curate with the keyboard shortcuts below, the
Organize tool and the Trash tool. Trash keeps its own separate deletion selection.
Virtual collections do not change file locations.

**Keyboard curation** works on the focused card, or the card under the pointer:
`1`-`5` rate, `0` clears the rating, `F` toggles favorite, `D` toggles the
deletion mark, `X` toggles selection (`Shift+X` selects a range), `Esc` clears the
selection and the arrow keys move focus between cards. Shortcuts are ignored while
typing in a field or while a dialog or the full screen viewer is open. When a marked
image is hidden by the active filter, the next image moves under the pointer, so
repeated `D` presses cull images in sequence.

Gallery cards retain filenames and pixel dimensions. A shield icon indicates
encrypted storage. Five interactive stars show each rating: click a star to save
that rating, or click the current rating again to clear it. Assigning a nonzero
rating or marking an image for deletion also marks it reviewed; clearing a rating
or removing a deletion mark does not change review status.

The **Organize** tool starts with a **Tags** section listing every tag on the selected
images as chips. A small count on a chip means only that many of the selected images
carry the tag. Click a chip's **x** to remove that tag from every selected image that
has it; other metadata is unchanged. Below it are the selected count and **Clear selection (ESC)**, followed by
independent Collections and Tags columns. Each has a labeled select for existing
values, with a **Create new** option for text entry. **Apply**, **Clear**, and
**Suggest (AI Vision)** share one row. Clear resets only that selection/input; Apply adds collection
membership or merges tags without discarding existing metadata. Vision suggestions
support up to 24 selected images and require individual approval. Applying a
collection suggestion changes only collections; applying a tag suggestion changes
only tags. Ratings and review status are edited with keyboard shortcuts and guided review.

**Unexposed features:** the workbench retains its side-by-side two-image comparison
code (including independent ratings), combined collection/tag application,
collection removal, and rating/review-status operations, but no tool exposes
controls for them. Saved views (named filter snapshots stored in encrypted SQLite
settings; saving the same name replaces a view, loading restores filters rather
than old results) are also retained in code but not shown in the UI. Filters still
support collections, minimum ratings, review status and unorganized images.

In the **Review** tool, set a **Goal** (default 10) and click **Start Review** to
review that many pending, unmarked images from the current filtered results across
all pages. The goal is capped at the number of eligible images; review is disabled
when none are available. Enable **Random order** to select a random sample without
duplicates and review it in shuffled order. Otherwise, the session takes the first
images in the current sort order. Narrow filters first to review a subset. The session sequence stays fixed
until closed.

The review panel centers a progress bar with **Skip** and **Undo** on one row.
Progress shows reviewed plus skipped images out of the session goal. On completion,
only progress and Undo remain, with an Escape-to-return message. Completing every
image without skipping triggers one confetti burst from each side, 80ms apart
(disabled when reduced motion is preferred).
Decision actions use keyboard shortcuts:

- K marks reviewed and clears any deletion mark, preserving rating/favorite.
- F marks reviewed, favorites the image and clears deletion marks.
- D marks reviewed and marks for later removal; it never deletes.
- 1-5 assigns that rating, marks reviewed and clears deletion marks.
- S / **Skip** advances without changing the image; skipped pending images stay pending.

Decisions persist immediately, update the awaiting-review count and card status,
and automatically advance. Arrows browse without
making decisions; go back to revise one. Rating or keep does not remove a favorite.
At the end, the summary reports reviewed, skipped and untouched counts. Reaching
the last image does not imply you reviewed images bypassed with the arrows:
use the Previous/Next controls to revisit untouched images. **Session complete**
means every image has a decision or explicit skip; skips remain pending in the library.
**Undo** restores the previous review status, rating, favorite and
deletion mark, and returns to that image. Multiple decisions can be undone in reverse
order while the session is open; skips are not undo entries, and permanent removal
is never undone. Escape/Close ends
the session and refreshes library queues. A failed save keeps the current image;
a failed next-image load reports the error without undoing an already saved decision.
Closing early preserves completed decisions, and untouched images remain pending.
Review uses the fast viewer's fit/full-size controls and bounded preloading.

The **Search** tool uses the local LLM to interpret a natural-language search into
explicit filters; review and apply the suggestion. This is filter interpretation,
**not semantic embeddings/vector search**.

The **Local intelligence** tool has **Play**/**Stop** and **Analyze selected**.
Play analyzes every image without a description or pending suggestion, one at a
time, until finished or stopped. Analyze selected handles at most 24 selected images with
the configured local vision model. Description, tags and collection name are
requested together in one vision request per image, without a second text-only call.
If only the collection field is missing or invalid, valid descriptions and tags
remain available for approval with an explicit warning; no collection is added.
Invalid description/tag fields reject the suggestion with a field-specific error.
It sends metadata-free previews plus descriptions/tags and existing collection
names to the configured loopback or private LAN endpoint. Stop takes effect after the current image.
Descriptions, tags and collection suggestions remain temporary until approved
individually: cards with a suggestion show a sparkles badge that opens the full
screen viewer, where **Approve & save** or **Dismiss** decide each one. Dismissing or locking discards them. Approval replaces the
description, merges tags and adds collection membership. AI never changes
ratings, review state, deletion marks, or files. Analysis only runs when you start it.
Bulk failures are reported per image rather than silently skipped.
The navigation job indicator stays visible when you change workflows, including
image progress, a stop-after-current-image control and an **Open job** link.
Completed suggestion counts remain visible until approved or dismissed. Stopping
does not interrupt the current request; its configured timeout still applies.
While an LLM job is running, locking, ingestion, removal, file moves and gallery
configuration changes are blocked. Browsing and review remain available. Stop the
job and wait for the current image to finish before changing files. Suggestions
for images removed from the current library are discarded.

Do not open the same gallery concurrently in multiple application instances.
No existing gallery is automatically copied or modified. For testing an existing
library, use a complete separate backup; stored image paths are absolute, so
pointing at a copied database alone still references original image files.

- **Ingest images** accepts a local folder path with optional recursive ingestion.
  **Supported Media** lists supported formats and limits. The **3rd party** section
  explains that external tools must be installed separately and shows a green/red
  FFMPEG availability badge under **Video encoding** (MP4 ingestion requires both
  ffmpeg and ffprobe).
  **Include subfolders** defaults on; **Delete original media** and
  **Save performance information** default off. Enabling deletion displays a
  permanent-deletion warning without an additional confirmation checkbox.
  The collapsed **Advanced** section offers HDD (default),
  SSD and Custom profiles, shows active worker/batch settings, and saves them per
  gallery. Only Custom exposes worker and batch-size inputs.
  Enter a source path and select **Scan** before ingesting. The scan caches supported
  file paths, sizes and modification times in memory, and reports image/video counts
  and total source size. **Include Images** and **Include Videos** recalculate the
  selected count and destination-space estimate without enumerating again. Videos
  are disabled when ffmpeg/ffprobe are unavailable. Changing the source or recursion,
  locking, or finishing an ingest discards the scan; newly added files require a
  new scan. Changed/missing selected files stop ingestion and require a rescan.
  **Ingest** is disabled for an empty selection or insufficient disk space.
  Free space is checked again immediately before
  ingestion on both media and catalog destinations. The conservative estimate
  allows 64 bytes per encrypted media file, 4 MiB per file for thumbnails/metadata
  and catalog/WAL growth, plus 16 MiB for catalog overhead. It is not an exact size
  prediction: duplicates may reduce usage and concurrent writes can consume space.
  The displayed estimated change is separate from that safety allowance: it uses
  source bytes plus the actual encryption envelope size (36 bytes each for media
  and thumbnail), a typical 128 KiB thumbnail allowance and 4 KiB catalog-entry
  allowance per file. These thumbnail/catalog allowances are estimates, not measured
  sizes; compression and metadata vary, and duplicates can reduce actual growth.
  There is no fixed catalog reserve in the displayed estimate. Selected source bytes
  are subtracted when deletion is enabled. Original deletion is not credited as
  available space for the safety check before verification.
  Browser folder/file uploads are not supported. PNG, JPEG,
  and WebP images (and MP4 videos, below) are supported; images up to 50 MiB, 40 megapixels, and 16384 pixels per
  dimension. Animated images are rejected. Imports are copies; originals
  remain plaintext and untouched by default. Exact source-byte duplicates are reported.
- Gallery images are not draggable. Dropping files or browser images into the app
  is blocked so native browser navigation cannot interrupt the unlocked gallery.
  Use **Ingest images** with a local folder path instead.
- Ingestion shows a scanning indicator, then a progress bar with processed/total
  files, imported images and per-file issues (including duplicates). The final
  counts remain visible after completion. Workers (1-32) and catalog batch size
  (1-64) are configurable and saved encrypted per gallery. Suggested **HDD / NAS**
  profile: 4 workers and 16 files per commit; **SSD / NVMe**:
  half the logical CPU count, bounded to 2-16 workers, and 16 files per commit.
  On a 32-CPU host this suggests 16 workers. These are starting points, not
  measured guarantees; lower workers for large images/videos or limited memory.
  A batch size below the worker count also limits effective concurrency.
  Files are read, decoded once, encrypted and written durably outside the catalog
  gate. Each decode has bounded allocations (256 MiB per allocation, 512 MiB
  cumulative); dimensions and animation limits remain enforced. Catalog duplicate
  checks and inserts remain serialized, with one durable transaction per bounded
  batch. A failed batch rolls back its rows, removes uncommitted ciphertext and
  retains its sources; earlier committed batches remain. Fingerprint migration runs
  once per folder job. Thumbnails are encrypted once and inserted as ciphertext.
  Progress callbacks are awaited and serialized but throttled to 100 ms, with
  errors and the final counts always reported.
- Local folder-path ingestion supports **Delete original media**,
  off by default, with a permanent-deletion warning. The saved encrypted image,
  catalog and thumbnail are reread and authenticated after the batch commits,
  using one catalog query on the existing import connection. The saved plaintext
  hash and image header dimensions are checked without a second pixel decode.
  On Windows, sources are opened with read/delete access while denying writes,
  renames and replacement. After verification, the exact held handle is marked
  for deletion and closed; there is no second source read or pathname deletion
  race. SMB servers must honor Windows sharing and handle-deletion semantics;
  access or deletion failures are reported, never bypassed. Other operating systems
  retain the conservative rename-and-rehash deletion path.
  Failed imports, duplicates and verification failures retain their sources.
  Deletion failures are reported even if the encrypted import succeeded.
  Do not edit, rename or replace source files during ingestion.
  Source deletion bypasses the Recycle Bin and is **not secure shredding**.
  SSD/NVMe wear leveling can leave old physical flash pages even after overwrites;
  TRIM is not a verifiable forensic-erasure guarantee. Backups, snapshots and
  synced copies are not deleted. Use full-volume encryption before plaintext
  is written. Whole-drive vendor-supported sanitize/cryptographic erase is a
  separate drive-retirement operation, not a gallery feature or per-file guarantee.
- Optional privacy-safe timings include the source, media and catalog drive kinds
  (never their paths), separate `DuplicateWait` and `DuplicateCheck`, `OpenSource`,
  `CloseSource`, `ReportProgress`, and `CatalogCommit`. `PersistWait` and
  `CatalogCommit` are measured per batch; other stages run on parallel workers.
  `File` includes time awaiting its batch and progress accounting, so overlapping
  stage totals are not wall-clock proportions.
- Images (PNG, JPEG and WebP) are stored as their **original bytes**, unchanged
  and never re-encoded or converted, in authenticated encrypted `.dpng` files.
  `.dpng` is the encrypted-image container for every image format; the actual
  format is recorded in the encrypted catalog. Other formats are rejected even
  with a supported extension. PNG text and EXIF metadata are extracted into the
  encrypted catalog. Thumbnails omit embedded metadata. Images imported by
  earlier versions remain the PNGs they were converted to.
- **MP4 videos** (MP4/MOV containers up to 256 MiB; browser playback needs a codec such as H.264) are imported
  when ffmpeg is installed. The gallery finds `ffmpeg`/`ffprobe` via the
  `Gallery:FFmpegPath` setting (folder or executable), then PATH, then WinGet links.
  The original bytes are encrypted unchanged as `.dmp4`; the first frame becomes the
  thumbnail and container metadata (including ComfyUI `prompt`/`workflow` and Video
  Helper Suite `comment` tags) is extracted into the encrypted catalog. ffprobe and
  ffmpeg need a file path, so each video is briefly copied as plaintext to
  `%TEMP%\gallery-<guid>.mp4` and deleted when import finishes (not securely
  shredded). Playback decrypts into an in-memory browser Blob URL; there is no
  plaintext server URL. Exact-duplicate grouping never mixes images and videos.
- **ComfyUI metadata** (PNG `prompt`/`workflow` text chunks, EXIF, and video tags)
  is parsed into prompt, negative prompt, CFG, steps and the checkpoint, UNet or
  diffusion model, by following the API prompt graph from the sampler. Results are
  shown in the image details and can be searched using the diffusion-model filter;
  existing records are backfilled automatically. UI-only workflows are detected
  but not parsed into fields.
- **Group by same prompt** (grid controls) groups exact positive-prompt
  matches, largest groups first, followed by unique and no-prompt images.
  **Show only these** or **Show all N with this exact prompt** filters to one prompt.
  **Group by diffusion model** groups images by their embedded checkpoint/model
  name, with images missing model metadata in a separate group. The **Diffusion
  model** filter supports partial, case-insensitive model-name searches. The
  **Media type** filter can limit the grid to videos.
- The **Download a copy** preview icon downloads a decrypted, cleaned copy in the stored format, without
  decoding pixels or transcoding video. Images keep standard image data plus EXIF,
  ICC, XMP and PNG text (compressed PNG text is written uncompressed as `tEXt`, so
  ComfyUI can read prompt/workflow); unknown or private chunks/segments and any bytes
  after the image end are dropped. MP4s keep all standard top-level boxes, including
  `moov` metadata such as the ComfyUI workflow; unknown top-level boxes (for example
  `uuid` vendor data) and `free`/`skip` padding are zero-filled in place, keeping
  sample offsets valid, and bytes after the last box are dropped. Boxes nested
  inside `moov` are not cleaned. Playback and in-app viewing use the stored bytes.
  **Save copies (zip)** exports a selection (up to 1 GiB). Exports are plaintext
  once saved; protect them accordingly.
- New imports use `images\<first two ID characters>\<next two ID characters>\<id>.dpng`
  beneath the gallery directory. Random IDs avoid filename collisions, and two-level
  sharding spreads files across directories. Original/display names need not be
  unique. Existing files and user-selected move destinations are not relocated.
- Duplicate detection uses a unique SQLite index on a 32-byte keyed fingerprint:
  HMAC-SHA256 of the binary source SHA-256, with a dedicated key derived from
  the gallery master key using HKDF-SHA256 and a versioned purpose label.
  Fingerprints are stable across passphrase changes but differ between galleries.
  A stolen database does not allow testing suspected content without the key,
  though fingerprints reveal content equality. Original hashes remain encrypted
  in image records. Matching is byte-exact, not perceptual.
  On first unlocked use, old catalogs add the index and transactionally backfill
  missing fingerprints from encrypted hashes, without reading or moving images.
  Later imports use indexed lookups rather than decrypting the entire catalog;
  a unique constraint and serialized insertion also prevent concurrent duplicates.
  Backfill failures roll back and are surfaced rather than silently skipping records.
- Filter by prompt/metadata/text, tag, source/current folder, vision and diffusion
  models, favorite, deletion mark, orientation, minimum dimensions, analysis status,
  and import date range. Sort and page results. Encrypted metadata is decrypted
  and filtered in application memory, not indexed as plaintext in SQLite.
  This favors privacy over large-library query scalability.
- Click a thumbnail to edit descriptions/tags, run local vision analysis,
  or move and rename its encrypted file into an **existing** folder. Files
  remain `.dpng`; existing destinations are never overwritten. Move copies
  ciphertext first, updates the catalog, then removes the old ciphertext.
  An interrupted move can leave an extra encrypted copy, not a lost original.
  If removal of the old copy fails, the UI reports both locations and the
  catalog continues to track the new file.
- **Full screen view** opens a viewport-filling viewer for a snapshot of the current
  page, also available in image details. Right arrow advances; left arrow goes back.
  D toggles the deletion mark, F toggles favorite,
  O displays full-resolution pixels at 100% with scrolling, P fits the image to
  the window without changing aspect ratio, and Escape closes the viewer.
  Navigation stops at page boundaries. Marks/favorites persist immediately, but
  do not remove images from the viewing sequence; filters refresh when it closes.
  Shortcuts are active only while viewing and ignore held-key repeats/modifiers.
  The viewer progressively preloads the current page into browser-memory Blob URLs,
  nearest neighbors first. Fit mode uses metadata-free JPEG previews (quality 85,
  maximum 1600 pixels per dimension, transparency composited on black). These are
  viewing copies only; the encrypted original is unchanged. Revisiting a cached image
  avoids decrypting, encoding, and transferring it again. Original-size mode loads
  the saved full-resolution image in its stored format on demand without re-encoding, caching at most two
  originals. Each cache has a 64 MiB encoded-image budget; the current image may
  exceed that budget. Decoded browser pixels consume additional memory.
  URLs are revoked on eviction/close; no plaintext disk cache is created by the app.
- Favorites and deletion marks persist. **Marking alone never deletes an image**;
  marks can be reversed. Only marked images can be permanently removed.
  Use **Remove this image** in details, or select marked cards in **Trash** using
  the standard card selection button or `X` (`Shift+X` selects a range), then choose
  **Delete Forever** in the Trash tool. Review the preview and explicitly confirm removal.
  **Restore** clears the deletion mark and returns images to the active library.
  Choose separate policies for accessible originals (retain or delete) and missing
  originals (restore PNG or discard without restoration). Bulk operations report
  each failure and continue with other images; they are not all-or-nothing.
  Original deletion checks its source hash and refuses changed files.
  Restoration verifies/decrypts the full image, writes and verifies `<id>.png` in an existing
  folder without overwriting. This restores decoded pixels and retained PNG metadata,
  not the exact JPEG/WebP source bytes. Previously ingested browser uploads do not expose original local
  paths, so their originals are considered inaccessible, even if they still exist.
  Legacy imports with absolute source folder paths are recognized.
  Removal deletes catalog metadata, thumbnails, fingerprint and encrypted image,
  allowing subsequent reimport. The catalog row is deleted and a file delete is queued
  in the same SQLite transaction; the queued delete then runs in the background.
  Interruptions can leave a restored PNG, or ciphertext that the queue deletes later.
- **Background file operations.** Slow drive work (deleting removed or moved-away
  ciphertext, indexing media folders, recovering removals interrupted by older
  versions) is stored as rows in a `drive_operations` table and played in batches in
  the background, so browsing stays available on large or network (SMB) galleries.
  The sidebar shows progress with **Pause**, **Resume**, **Retry failed** and **Cancel**.
  Pausing or closing the app loses no work. Cancelling discards queued work and leaves
  the affected files on disk. Opposing operations are pruned; for example, moving an
  image back to a folder whose delete is still queued reuses the existing file. Drive
  errors retry with backoff and are then reported as failed without changing files.
  Queue rows contain only keyed path hashes and encrypted payloads. Like ingestion,
  queued work continues after the gallery locks.
- **File index.** The first load after upgrading schedules one scan per directory,
  instead of a full-tree walk, to build a `media_index` table of encrypted file
  sizes. The storage overview then reads that index instead of the drive. A missing
  directory counts as empty; a failing directory retries on its own. Because the
  gallery owns its media folders, files changed outside it are only noticed after
  **Rebuild file index** in **Settings > Gallery overview**. The same scans find
  `.removing-*` residue from interrupted removal operations. Recovery restores a
  staged copy only if it authenticates as that image. It deletes identical or
  orphaned copies, and preserves conflicting copies as failed operations.
  Gallery loading composes a catalog read, fingerprint backfill, and generation-metadata
  migration, then schedules this background work. `ListAsync` only reads catalog records.
  Vault move and purge still enumerate the whole gallery folder.
  Retained backups/copies are not removed.
  Permanent removal bypasses the Recycle Bin and is not secure SSD/NVMe erasure.
- Configure a loaded vision-capable model and a literal loopback or private LAN,
  OpenAI-compatible endpoint under **Settings > LLM endpoints**. Examples:
  LM Studio `http://127.0.0.1:1234/v1/`, Ollama
  `http://127.0.0.1:11434/v1/`. Requests are user-triggered, use a metadata-free
  preview of at most 1600 pixels per dimension, and store structured descriptions
  and tags. Redirects, proxies, public IPs, and hostname-based endpoints
  are not permitted. A running local or private-LAN vision model is required;
  none is bundled.

## Encryption and recovery

Each gallery has a cryptographically random 256-bit master key. AES-256-GCM
encrypts image files, SQLite record blobs, thumbnail blobs, and vision settings,
with fresh random 96-bit nonces and 128-bit authentication tags. Authenticated
context binds each blob to its record ID and purpose, preventing substitution
between images, metadata, and thumbnails.

The master key is independently wrapped by two keys derived with
PBKDF2-HMAC-SHA256 (600,000 iterations, independent 256-bit salts): one from the
passphrase and one from a cryptographically random recovery phrase. The phrase
contains 32 compound words from a 256-entry vocabulary (256 bits of entropy).
It is an application-specific recovery format, **not a BIP39 wallet seed**.
Only salts and authenticated wrapped keys are saved in `vault.json`; neither
credential nor an unwrapped master key is saved to disk.

Use the recovery-words tab to unlock after forgetting a passphrase, then change
the passphrase in **Settings > Gallery security** (recovery unlock opens it directly). Recovery words remain valid. They are
shown only during creation and cannot be displayed again. There is **no
backdoor, email reset, escrow, or machine-bound bypass**. Losing both credentials
makes the gallery's encrypted content unrecoverable, assuming strong credentials
and uncompromised cryptography. Damage or loss of the gallery header is also
unrecoverable without its backup.

Use **Settings > Backups** to create consistent live snapshots of `vault.json`,
`gallery.db`, and all catalogued encrypted media. A full backup plus its incremental
chain is needed to reconstruct the latest snapshot; keep every archive and companion
manifest. A passphrase change affects only the current header; backups made before
that change can still be unlocked using the old passphrase.
Stored file paths are absolute: moving a gallery to another machine/drive is not
an automatic portable-library migration.

Keys remain in memory only for the unlocked browser circuit and in-flight
operations. Use **Panic** (`Ctrl+Shift+L`), reload, or disconnect to clear the session key.
Tabs unlock independently; lock every unlocked tab or close the process.
**Inactivity auto-lock** automatically locks the gallery, releases its checkout after active
operations finish, and zeroes keys from memory after a period of inactivity (default 15 minutes, configurable in **Settings > Gallery security**
to 5, 15, 30, 60 minutes or disabled).
**Panic / Emergency quick-lock** (`Ctrl+Shift+L`, `Alt+Shift+L`, or `Shift+Esc`, or the **Panic** button)
immediately locks the gallery, releases its checkout after active work finishes, zeroes encryption keys in RAM, revokes all decrypted media blob URLs,
and blanks all media elements in the browser DOM.
Plaintext exists in browser/server memory while viewing
and in the local vision server while analyzing. Browser extensions, malware,
swap files, process dumps, screenshots, and plaintext source files are outside
this at-rest encryption guarantee. SQLite reveals opaque IDs and blob sizes;
the filesystem reveals encrypted file names, locations, sizes, and timestamps.
Use neutral names if those are sensitive.

## Checks

```powershell
dotnet test .\src\Gallery.slnx
node --test .\src\tests\viewer-drag.test.cjs .\src\tests\viewer-keys.test.cjs
```

Tests cover credential/recovery rotation, authentication/tampering, encrypted
image/catalog/thumbnail storage, duplicates, folder ingestion, move/rename
safety, filtering, local endpoint restrictions, and vision request handling.
Curation tests cover perceptual near-duplicate grouping, keeper choice, encrypted
fingerprint/scan/preference caches, bulk updates and grid keyboard shortcuts.
Storage tests also cover sharded paths, same-name imports, gallery-specific
fingerprints, legacy-schema backfill and rollback, indexed query plans,
concurrent duplicate imports, and SQLite uniqueness enforcement.
