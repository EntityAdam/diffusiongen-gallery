CREATE TABLE catalog_format(id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL);
-- next
INSERT INTO catalog_format VALUES(1,1);
-- next
CREATE TABLE media(
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    original_name TEXT NOT NULL,
    stored_path TEXT NOT NULL,
    source_folder TEXT NOT NULL,
    source_path TEXT NOT NULL,
    source_created_at TEXT,
    imported_at TEXT NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    original_bytes INTEGER NOT NULL,
    source_sha256 TEXT NOT NULL,
    content_sha256 TEXT NOT NULL,
    description TEXT NOT NULL,
    user_description TEXT NOT NULL,
    tags TEXT NOT NULL,
    analysis_model TEXT NOT NULL,
    is_favorite INTEGER NOT NULL,
    is_marked_for_deletion INTEGER NOT NULL,
    rating INTEGER NOT NULL,
    is_reviewed INTEGER NOT NULL,
    media_type TEXT NOT NULL,
    content_type TEXT NOT NULL,
    duration_seconds REAL NOT NULL,
    imported_ticks INTEGER NOT NULL,
    source_identity TEXT NOT NULL UNIQUE
) STRICT;
-- next
CREATE TABLE media_metadata(media_id TEXT PRIMARY KEY NOT NULL REFERENCES media(id) ON DELETE CASCADE, document TEXT NOT NULL, parser_version INTEGER NOT NULL) STRICT;
-- next
CREATE TABLE media_generation(
    media_id TEXT PRIMARY KEY NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    prompt TEXT NOT NULL,
    negative_prompt TEXT NOT NULL,
    cfg REAL,
    steps INTEGER,
    model TEXT NOT NULL,
    model_kind TEXT NOT NULL,
    sampler TEXT NOT NULL,
    scheduler TEXT NOT NULL,
    has_workflow INTEGER NOT NULL,
    has_api_prompt INTEGER NOT NULL
) STRICT;
-- next
CREATE TABLE media_collections(media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE, ordinal INTEGER NOT NULL, name TEXT NOT NULL, PRIMARY KEY(media_id,ordinal)) STRICT;
-- next
CREATE TABLE media_thumbnails(media_id TEXT PRIMARY KEY NOT NULL REFERENCES media(id) ON DELETE CASCADE, bytes BLOB NOT NULL) STRICT;
-- next
CREATE INDEX media_browse ON media(is_marked_for_deletion,imported_ticks DESC);
-- next
CREATE INDEX media_browse_all ON media(imported_ticks DESC);
-- next
CREATE TABLE settings (
    id TEXT PRIMARY KEY NOT NULL,
    payload TEXT NOT NULL
) STRICT;
-- next
CREATE TABLE IF NOT EXISTS drive_operations (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,
    target BLOB NOT NULL,
    image_id TEXT,
    payload BLOB NOT NULL,
    state TEXT NOT NULL DEFAULT 'pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    not_before INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    revision INTEGER NOT NULL DEFAULT 0
);
-- next
CREATE UNIQUE INDEX IF NOT EXISTS drive_operations_kind_target ON drive_operations(kind, target);
-- next
CREATE INDEX IF NOT EXISTS drive_operations_due ON drive_operations(state, not_before, sequence);
-- next
CREATE TABLE IF NOT EXISTS media_index (
    target BLOB PRIMARY KEY,
    directory BLOB NOT NULL,
    length INTEGER NOT NULL
);
-- next
CREATE INDEX IF NOT EXISTS media_index_directory ON media_index(directory);
-- next
CREATE TABLE IF NOT EXISTS storage_overview_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    revision INTEGER NOT NULL
);
-- next
INSERT OR IGNORE INTO storage_overview_state (id, revision) VALUES (1, 0);
-- next
CREATE TRIGGER media_revision_insert AFTER INSERT ON media BEGIN UPDATE storage_overview_state SET revision=revision+1 WHERE id=1; END;
-- next
CREATE TRIGGER media_revision_update AFTER UPDATE ON media BEGIN UPDATE storage_overview_state SET revision=revision+1 WHERE id=1; END;
-- next
CREATE TRIGGER media_revision_delete AFTER DELETE ON media BEGIN UPDATE storage_overview_state SET revision=revision+1 WHERE id=1; END;
