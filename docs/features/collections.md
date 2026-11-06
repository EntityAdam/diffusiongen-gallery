# Collections and sets

The organization workspace follows Collection → Sets → Media. Collections and sets can exist without media. A media record has one optional `SetId`; `Collections` is a compatibility projection of the owning collection name for existing gallery filters. New placement goes through `MoveMediaToSetAsync`, never through legacy collection-name edit callbacks.

Collection definitions are encrypted settings (`collection-hierarchy:v1`). Placement, ordering, cascading name projections and saved collection filters commit in the same transaction. SQLite uses the existing encrypted record envelope. Turso's ordered DbUp migration adds typed placement columns to its encrypted database. Backups include both definitions and media placement.

On first adoption, single legacy memberships become an `Imported media` set in their collection. Ambiguous memberships retain every prior name in `LegacyCollections` and appear in Unorganized for explicit placement. Moving one clears those migration hints; Undo restores them. Nothing chooses between multiple old assignments automatically.

Deleting a collection or set preserves files, tags and ratings, returning affected media to Unorganized. Undo restores organization only, preserving subsequent tag/rating/description edits. A later organization change or removal of affected media invalidates that Undo token rather than overwriting newer work.

The gallery toolbox, fullscreen and guided review share `CollectionDestinationDialog`. It uses the existing Advanced Select for both collection and scoped set selection, with explicit inline creation. Creating a container persists it even if the later move is canceled. Fullscreen/review `C` opens this picker; `T` edits tags and `1–5` rate. Quick edits do not advance the review queue.

Destination AI only proposes an existing set within the selected collection, using available descriptions and tags. Its system prompt is saved under Settings → LLM endpoints. A suggestion requires user selection and an explicit Move; unknown set IDs are rejected. Description/tag analysis no longer proposes collection ownership through the workbench.

The approved design and interaction specification are in [Figma](https://www.figma.com/design/N8p6b65oOuGKvAWmxbGBs2/Advanced-Select?node-id=17-239).
