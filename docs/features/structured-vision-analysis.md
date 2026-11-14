# Experimental structured vision analysis

The first-pass experiment describes an image using a commented TypeScript interface in the editable vision system prompt. It returns a scene overview, entities with free-form attributes, optional relationships, optional medium/style/artist information, existing tag selections, and proposed new tags. There is no fixed list of colors, objects, or styles.

## Enable

1. Open **Settings → LLM endpoints**.
2. Enable **Experimental scene analysis**. The commented TypeScript prompt is selected automatically.
3. In **Prompts**, choose a task to edit its instructions. **Restore default prompt** resets only the selected task.
4. Adjust **Structured vision response token limit** if needed (default 8,192), then save settings.
5. Analyze a detail item, a selection, or media without a description through the existing vision workflow. Review and approve the encrypted draft.

Each image-analysis mode remembers its own custom prompt. Switching modes restores that mode’s prompt; switching back restores your previous prompt, including after saving and reopening settings. Existing settings default to the legacy description/tags format, with both tag controls enabled. Model discovery, local/LAN connection policy, timeouts, temperatures, set destination prompts, and existing generation limits are preserved. Structured requests have their own response token limit.

## Tags

**Choose from existing tags** and **Suggest new tags** are independent:

| Existing | New | Allowed assignments |
|---|---|---|
| On | On | Relevant registered tags and novel suggestions |
| On | Off | Registered tags only |
| Off | On | Novel suggestions only; the catalog is not sent |
| Off | Off | None; descriptions and structured facts still work |

The whole registered catalog, including unused tags, is sent only when existing-tag reuse is enabled. The application filters output according to the settings and rechecks current catalog membership at approval. Registered spelling and case-insensitive deduplication are preserved. Tags merge with existing assignments; neither checkbox removes existing tags. New assignments and new definitions are saved only on approval.

The first-pass template requires top-level `tags` for existing selections and `suggested_tags` for novel proposals, returned alongside `scene` and `entities` in the same request. Use an empty array when that option is disabled or there are no relevant assignments. Missing or malformed tag arrays produce review warnings while usable scene analysis is preserved. They are classified against the catalog even if the model puts a name in the wrong array. Optional `scene.booru_tags` are booru-style visual descriptors retained as metadata only; they never create gallery assignments. Previously saved `helper_tags` metadata remains intact in the original JSON. Existing custom prompts remain editable; **Restore default prompt** loads the updated TypeScript definition. Tag-only approval preserves descriptions, model attribution, and accepted structured analysis.

## Validation and review

The response must be valid JSON with a top-level `scene` object and `entities` array. `relationships` may be omitted; if present it must be an array. An empty entities array is valid. Documents are limited to 512 KiB and normal JSON parser depth limits. JSON code fences remain supported, but automatic repair or synthesis of missing required sections is not implemented.

Nested details are best effort. Missing attributes, duplicate IDs, malformed nested entries, and unresolved entity/sub-part references produce visible warnings. Original JSON, including unknown fields, is preserved. Unusable facts are excluded from the search projection rather than causing the entire document to be discarded. Dotted IDs such as `subject1.rightHand` are opaque identifiers; hierarchy is not inferred from the dots.

`scene.description` supplies the readable overview. Entity descriptions and attributes are displayed separately in details and in the fullscreen suggestion panel. Accepted analysis is also inspectable from the fullscreen viewer. Missing/empty overviews preserve existing vision notes; overviews longer than 10,000 characters are shortened with a warning while the full JSON remains intact. Full approval records the model and merges eligible tags without changing user notes, ratings, review status, collections, or sets. A draft is rejected if its source image, source vision notes, or accepted structured revision changed while it was pending.

## Structured filters

Under **Filters → Vision analysis**, enter a subject kind, relationship, object kind, attribute name, and attribute value. For example, when those labels appear in an annotation:

```text
Subject: man
Relationship: wearing
Object: hat
Attribute: color
Value: yellow
```

Kinds, attribute names, and relationships match case-insensitively as exact labels; attribute values match a case-insensitive phrase. This is deterministic filtering, not natural-language interpretation or semantic normalization. The kind and relationship inputs suggest observed labels from your library. Use the labels actually returned by your model (`person` versus `man`, for example). With an object or relationship, all attribute conditions apply to the same target object; otherwise they apply to the subject. Separate people and hats cannot satisfy one relationship accidentally.

Structured filters combine with ordinary gallery filters and can be saved as views. They work on accepted first-pass facts without another model call. Legacy-only items and unresolved facts cannot satisfy a positive structured query. Missing annotations or omitted details are not evidence of absence; negative searches are not provided.

## Storage and next steps

Full JSON and provenance are loaded on demand from a separate catalog table. The browse record contains extracted entity/relationship facts, not the raw JSON or prompt. SQLite artifacts use authenticated per-item encryption; Turso artifacts remain within the existing page-encrypted catalog. Approval atomically updates the record, tag catalog, and artifact. Backup/restore includes the table, and physical media removal deletes its artifact. Use **Download analysis JSON** in the accepted-analysis view to export the original document without embedding it into media files.

This milestone adds no question/audit pass or final verification pass. Those remain optional follow-up work after first-pass evaluation, along with broader normalization, malformed-output recovery, provider-enforced schemas, and TypeScript-to-C# source generation. Video analysis retains its existing thumbnail-based behavior and records that input in provenance; it does not describe an entire video.
