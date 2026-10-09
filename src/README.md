# Diffusion Gen Gallery 02

A standalone local-only .NET 10 / Blazor Server gallery. It shares `src03`'s
charcoal, mint, typography, and Diffusion Gen branding, but has no project or
runtime dependency on it. `gallery01` is frozen; all new development lives here.
This is an independent source copy, not a shared project.

## Run

Install the .NET 10 SDK, then from the repository root:

```powershell
dotnet run --project .\src\gallery02\Gallery.csproj
```

Open **http://127.0.0.1:5189**. Create a vault with a unique passphrase of at least
14 characters and **save the 32 recovery words offline before continuing**.
The default vault is `%LOCALAPPDATA%\DiffusionGenGallery02`. To choose a different
location, set `Gallery__DataDirectory` before launching:

```powershell
$env:Gallery__DataDirectory = 'D:\PrivateGallery'
dotnet run --project .\src\gallery02\Gallery.csproj
```

The process binds to loopback and rejects non-loopback clients, untrusted Host
headers, and cross-origin requests. Do not expose it through a proxy, tunnel,
or public port. This is a single-owner local application, not a network service.
Only one process should operate on a vault at a time.

## Workflow

### Smart library, curation and discovery

**Settings > LLM endpoints** borrows src03's connection workflow: enter the API base URL,
an optional bearer API key and a request timeout (10-3600 seconds, default 180).
**Fetch models** tests the entered connection and populates the model selector
without saving settings. You can also enter a model ID manually. Discovery does
not verify vision capabilities; select a vision model for image analysis.
**Save LLM settings** stores the configuration, including the key, encrypted in
the vault. Unsaved form edits are isolated from the active configuration; leaving
the endpoint workflow discards them. The same saved connection, key and timeout apply to image analysis, collection
suggestions and natural-language filter interpretation. Literal loopback and RFC 1918
private LAN addresses are allowed: `10.0.0.0/8`, `172.16.0.0/12` (172.16 through
172.31), and `192.168.0.0/16`. Use the server's actual IP, not a network base address.
Hostnames, public addresses, other IPv6 addresses, redirects and proxies remain
blocked. Use `127.0.0.1` rather than `localhost`. A LAN endpoint receives decrypted
previews and metadata; trust the server and use HTTPS when available, since HTTP
does not encrypt traffic in transit. The gallery web application itself still binds
only to loopback. Gallery02 does not import or share src03's configuration.

The left navigation separates Library, Guided review, Organize, Explore & saved
views, Local intelligence, Remove marked images, Ingest images and Settings.
**Settings** opens on a **Gallery overview** with tabs for **Duplicates**, **LLM endpoints** and
**Vault security**. The overview shows database size (`gallery.db` plus its WAL/SHM
files, which include encrypted thumbnails), total file count (catalogued encrypted
image files plus vault header/database files) and total size on disk, auto-scaled
from B through PB. Measurements are cached encrypted in the vault with a "Last
updated" time and are only recalculated when you select **Refresh** (or the first
time no cached overview exists). Catalogued images missing from disk are reported
and excluded from the totals. The overview also summarizes the last duplicate scan.

**Settings > Duplicates** finds likely duplicates, including resized or
re-encoded copies that import's exact-byte check cannot catch. Each image gets a
64-bit perceptual difference hash computed from its decrypted thumbnail;
images within 6 of 64 bits and with a similar aspect ratio (or identical PNG
pixels) are grouped. Fingerprints and the last scan result are cached encrypted
in the vault, and rescans only fingerprint new or changed images. Each group shows
thumbnails, sizes and approximate reclaimable space, and recommends a keeper:
unmarked first, then highest rating, favorite, most pixels, largest original and
oldest import. **Mark other(s) for deletion** (per group) or **Mark all extra
copies** only sets reversible deletion marks; permanently remove them from
**Remove marked images**.

**Review in full screen** (or **Review & choose keepers** on a group) opens a
full screen duplicate review. Step through each copy at full size, see its
dimensions, file size, rating and favorite status, and choose **one or more**
copies to keep from the filmstrip or with `K`. **Keep only this** and **Keep all**
are quick picks. **Apply** unmarks the kept copies, marks the rest for deletion
and moves to the next group; **Skip group** leaves a group unchanged. Reopening a
group you partly marked earlier restores those choices. Shortcuts: Left/Right
copies, Up/Down groups, `K` toggle keep, `A` apply, `S` skip, `O` 100%, `P` fit,
`Esc` close.
Only tools for the current workflow are shown. Image workflows reuse the same
grid, sort and collapsible **Filter images** controls. Filters carry across
workflows; removal temporarily resets filters to show all marked images, and leaving
removal restores your previous browsing filters. Active-filter chips remain visible
above results even when filters are collapsed. Remove individual chips or clear all
filters (the removal workflow keeps its marked-image default). The Library dashboard opens queues for all images, pending review,
favorites and images without a collection.

**Quick filters** above the grid toggle the most common views with one click:
**Unreviewed**, **5★**, **Favorites** and **Not analyzed**, each with a live count.
They combine with each other and with the full filter panel, and appear in the
active-filter chips like any other filter.

**Thumbnail size** (Compact, Comfortable, Large) sits next to the sort control.
Compact shows 48 smaller cards per page with condensed details, Comfortable shows
24 and Large shows 12. The choice is saved encrypted per vault and restored on
unlock.

Every gallery workflow except removal has a select button on each card. Click it to
toggle an image, or Shift+click to select the range from the last selected image
(across pages, in the current sort order). **Select page** adds the current page.
Selections remain across pages until cleared, and switching workflows clears them.
While images are selected, a floating **bulk action bar** rates (1-5★ or clear),
favorites/unfavorites, marks reviewed, marks/unmarks deletion, merges tags,
adds the selection to a collection, or runs **Find duplicates**. Find duplicates
compares the selected images against the whole library, reusing cached
fingerprints, and opens every matching group in the full screen duplicate review.
Removal keeps its own separate deletion
selection. Virtual collections do not change file locations.

**Keyboard curation** works on the focused card, or the card under the pointer:
`1`-`5` rate, `0` clears the rating, `F` toggles favorite, `Delete` toggles the
deletion mark, `X` toggles selection (`Shift+X` selects a range), `Esc` clears the
selection and the arrow keys move focus between cards. Shortcuts are ignored while
typing in a field or while a dialog or the full screen viewer is open. When a marked
image is hidden by the active filter, the next image moves under the pointer, so
repeated `Delete` presses cull images in sequence.

Gallery cards retain filenames and pixel dimensions. A shield icon indicates
encrypted storage. Five interactive stars show each rating: click a star to save
that rating, or click the current rating again to clear it. Card ratings do not
change review status, favorites or deletion marks.

**Organize** adds/removes collection membership, merges tags without
discarding existing tags, and applies 0-5 ratings and reviewed/pending status.
Rating and review status independently default to **Leave unchanged** so rating
images cannot accidentally reset their review state, or vice versa.
Select exactly two images for side-by-side comparison, with independent rating
buttons that also mark each image reviewed. Filters support collections,
minimum ratings, review status and unorganized images; sort by highest rating.

In **Guided review**, **Start guided review** opens the pending, unmarked images in the current filtered
results across all pages, in the current sort order. The count on the button is
the session size. Use the Awaiting review dashboard queue for the whole pending
library, or narrow filters first. The session sequence stays fixed until closed.

- K / **Keep** marks reviewed and clears any deletion mark, preserving rating/favorite.
- F / **Favorite** marks reviewed, favorites the image and clears deletion marks.
- D / **Mark deletion** marks reviewed and marks for later removal; it never deletes.
- 1-5 assigns that rating, marks reviewed and clears deletion marks.
- S / **Skip** advances without changing the image; skipped pending images stay pending.

Decisions persist immediately, update the awaiting-review count and card status,
and automatically advance. Arrows browse without
making decisions; go back to revise one. Rating or keep does not remove a favorite.
At the end, the summary reports reviewed, skipped and untouched counts. Reaching
the last image does not imply you reviewed images bypassed with the arrows:
**Continue remaining** returns to the first untouched image. **Session complete**
means every image has a decision or explicit skip; skips remain pending in the library.
**Undo last decision** restores the previous review status, rating, favorite and
deletion mark, and returns to that image. Multiple decisions can be undone in reverse
order while the session is open; skips are not undo entries, and permanent removal
is never undone. Escape/Close ends
the session and refreshes library queues. A failed save keeps the current image;
a failed next-image load reports the error without undoing an already saved decision.
Closing early preserves completed decisions, and untouched images remain pending.
Review uses the fast viewer's fit/full-size controls and bounded preloading.

**Explore & saved views** stores named snapshots of all sidebar filters in
encrypted SQLite settings. Saving the same name replaces that view. Loading a
view restores its filters, not its old results. The local LLM can interpret a
natural-language search into explicit filters; review and apply the suggestion.
This is filter interpretation, **not semantic embeddings/vector search**.

**Local intelligence** analyzes at most 24 selected images sequentially with
the configured local vision model. Description, tags and collection name are
requested together in one vision request per image, without a second text-only call.
If only the collection field is missing or invalid, valid descriptions and tags
remain available for approval with an explicit warning; no collection is added.
Invalid description/tag fields reject the suggestion with a field-specific error.
It sends metadata-free previews plus descriptions/tags and existing collection
names to the configured loopback or private LAN endpoint. Stop takes effect after the current image.
Descriptions, tags and collection suggestions remain temporary until approved
individually; dismissing or locking discards them. Approval replaces the
description, merges tags and adds collection membership. AI never changes
ratings, review state, deletion marks, or files. No automatic background jobs.
Bulk failures are reported per image rather than silently skipped.
The navigation job indicator stays visible when you change workflows, including
image progress, a stop-after-current-image control and an **Open job** link.
Completed suggestion counts remain visible until approved or dismissed. Stopping
does not interrupt the current request; its configured timeout still applies.
While an LLM job is running, locking, ingestion, removal, file moves and vault
configuration changes are blocked. Browsing and review remain available. Stop the
job and wait for the current image to finish before changing files. Suggestions
for images removed from the current library are discarded.

The default port and vault are separate from gallery01. Do not run both apps
against the same vault concurrently. No existing vault is automatically copied
or modified. For testing an existing library, use a complete separate backup;
stored image paths are absolute, so pointing at a copied database alone still
references original image files. Keep gallery01 on its own original vault.

- **Ingest images** accepts a local folder path with optional recursive ingestion.
  Browser folder/file uploads are not supported. PNG, JPEG,
  and WebP images (and MP4 videos, below) are supported; images up to 50 MiB, 40 megapixels, and 16384 pixels per
  dimension. Animated images are rejected. Imports are copies; originals
  remain plaintext and untouched by default. Exact source-byte duplicates are reported.
- Gallery images are not draggable. Dropping files or browser images into the app
  is blocked so native browser navigation cannot interrupt the unlocked gallery.
  Use **Ingest images** with a local folder path instead.
- Ingestion shows a scanning indicator, then a progress bar with processed/total
  files, imported images and per-file issues (including duplicates). The final
  counts remain visible after completion. At most two images are processed in
  parallel; catalog insertion and duplicate checks remain serialized. Fingerprint
  migration runs once per folder job. Fast lossless PNG compression reduces
  encoding time without changing decoded pixels or retained metadata, at the cost
  of potentially larger encrypted files. Existing encrypted images are unchanged.
- Local folder-path ingestion supports **Delete originals after verified import**,
  off by default, with an explicit acknowledgement. The saved encrypted image,
  catalog and thumbnail are reread and authenticated; the saved PNG hash and
  dimensions are checked before deletion. The source hash is checked again.
  Failed imports, duplicates and verification failures retain their sources.
  Deletion failures are reported even if the encrypted import succeeded.
  Do not edit, rename or replace source files during ingestion.
  Source deletion bypasses the Recycle Bin and is **not secure shredding**.
  SSD/NVMe wear leveling can leave old physical flash pages even after overwrites;
  TRIM is not a verifiable forensic-erasure guarantee. Backups, snapshots and
  synced copies are not deleted. Use full-volume encryption before plaintext
  is written. Whole-drive vendor-supported sanitize/cryptographic erase is a
  separate drive-retirement operation, not a gallery feature or per-file guarantee.
- Images are converted losslessly from their decoded pixels to PNG and stored
  as authenticated encrypted `.dpng` files. PNG text and EXIF metadata are
  extracted into the encrypted catalog; original JPEG/WebP byte streams are
  not preserved. Thumbnails omit embedded metadata.
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
  shown in the image details and are searchable; existing records are backfilled
  automatically. UI-only workflows are detected but not parsed into fields.
- **Group by same prompt** (grid controls) groups exact positive-prompt
  matches, largest groups first, followed by unique and no-prompt images.
  **Show only these** or **Show all N with this exact prompt** filters to one prompt.
  The **Media type** filter and **Videos** quick filter limit the grid to videos.
- **Save a copy** downloads a decrypted copy: videos as the original MP4, images
  as PNG with prompt/workflow kept in uncompressed `tEXt` chunks. **Export
  workflow** downloads the embedded ComfyUI workflow (or API prompt) as JSON.
  **Save copies (zip)** exports a selection (up to 1 GiB). Exports are plaintext
  once saved; protect them accordingly.
- New imports use `images\<first two ID characters>\<next two ID characters>\<id>.dpng`
  beneath the vault directory. Random IDs avoid filename collisions, and two-level
  sharding spreads files across directories. Original/display names need not be
  unique. Existing files and user-selected move destinations are not relocated.
- Duplicate detection uses a unique SQLite index on a 32-byte keyed fingerprint:
  HMAC-SHA256 of the binary source SHA-256, with a dedicated key derived from
  the vault master key using HKDF-SHA256 and a versioned purpose label.
  Fingerprints are stable across passphrase changes but differ between vaults.
  A stolen database does not allow testing suspected content without the key,
  though fingerprints reveal content equality. Original hashes remain encrypted
  in image records. Matching is byte-exact, not perceptual.
  On first unlocked use, old catalogs add the index and transactionally backfill
  missing fingerprints from encrypted hashes, without reading or moving images.
  Later imports use indexed lookups rather than decrypting the entire catalog;
  a unique constraint and serialized insertion also prevent concurrent duplicates.
  Backfill failures roll back and are surfaced rather than silently skipping records.
- Filter by prompt/metadata/text, tag, source/current folder, vision model,
  favorite, deletion mark, orientation, minimum dimensions, analysis status,
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
  viewing copies only; the encrypted PNG is unchanged. Revisiting a cached image
  avoids decrypting, encoding, and transferring it again. Original-size mode loads
  the saved full-resolution PNG on demand without re-encoding, caching at most two
  originals. Each cache has a 64 MiB encoded-image budget; the current image may
  exceed that budget. Decoded browser pixels consume additional memory.
  URLs are revoked on eviction/close; no plaintext disk cache is created by the app.
- Favorites and deletion marks persist. **Marking alone never deletes an image**;
  marks can be reversed. Only marked images can be permanently removed.
  Use **Remove this image** in details, or select marked cards and **Remove selected**.
  **Select filtered marked images** selects all matching marked images across pages,
  not just the visible page. Review the preview and explicitly confirm removal.
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
  allowing subsequent reimport. Ciphertext is staged before catalog removal; database
  errors attempt to put it back. Filesystem and SQLite changes are not a single atomic
  transaction: interruptions can leave `.removing-*` ciphertext or a restored PNG.
  Cleanup failures report the residue path. Retained backups/copies are not removed.
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

Each vault has a cryptographically random 256-bit master key. AES-256-GCM
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
the passphrase in **Settings > Vault security** (recovery unlock opens it directly). Recovery words remain valid. They are
shown only during creation and cannot be displayed again. There is **no
backdoor, email reset, escrow, or machine-bound bypass**. Losing both credentials
makes the vault's encrypted content unrecoverable, assuming strong credentials
and uncompromised cryptography. Damage or loss of the vault header is also
unrecoverable without its backup.

Back up `vault.json`, `gallery.db` (including any live SQLite WAL), and **all**
`.dpng` files, including files moved outside the default vault folder. Prefer
backups while the application is stopped. A passphrase change affects only the
current header; old backups can still be unlocked using their old passphrase.
Stored file paths are absolute: moving a vault to another machine/drive is not
an automatic portable-library migration.

Keys remain in memory only for the unlocked browser circuit and in-flight
operations. **Lock vault**, reload, or disconnect to clear the session key.
Tabs unlock independently; lock every unlocked tab or close the process.
There is no idle timer. Plaintext exists in browser/server memory while viewing
and in the local vision server while analyzing. Browser extensions, malware,
swap files, process dumps, screenshots, and plaintext source files are outside
this at-rest encryption guarantee. SQLite reveals opaque IDs and blob sizes;
the filesystem reveals encrypted file names, locations, sizes, and timestamps.
Use neutral names if those are sensitive.

## Checks

```powershell
dotnet test .\src\gallery02\tests\Gallery.Tests\Gallery.Tests.csproj
node --test .\src\tests\viewer-drag.test.cjs .\src\tests\viewer-keys.test.cjs
```

Tests cover credential/recovery rotation, authentication/tampering, encrypted
image/catalog/thumbnail storage, duplicates, folder ingestion, move/rename
safety, filtering, local endpoint restrictions, and vision request handling.
Curation tests cover perceptual near-duplicate grouping, keeper choice, encrypted
fingerprint/scan/preference caches, bulk updates and grid keyboard shortcuts.
Storage tests also cover sharded paths, same-name imports, vault-specific
fingerprints, legacy-schema backfill and rollback, indexed query plans,
concurrent duplicate imports, and SQLite uniqueness enforcement.
