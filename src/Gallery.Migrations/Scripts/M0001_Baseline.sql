CREATE TABLE IF NOT EXISTS images (
    id TEXT PRIMARY KEY,
    record BLOB NOT NULL,
    thumbnail BLOB NOT NULL
);
CREATE TABLE IF NOT EXISTS settings (
    id TEXT PRIMARY KEY,
    payload BLOB NOT NULL
);
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
CREATE UNIQUE INDEX IF NOT EXISTS drive_operations_kind_target ON drive_operations(kind, target);
CREATE INDEX IF NOT EXISTS drive_operations_due ON drive_operations(state, not_before, sequence);
CREATE TABLE IF NOT EXISTS media_index (
    target BLOB PRIMARY KEY,
    directory BLOB NOT NULL,
    length INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS media_index_directory ON media_index(directory);
CREATE TABLE IF NOT EXISTS storage_overview_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    revision INTEGER NOT NULL
);
INSERT OR IGNORE INTO storage_overview_state (id, revision) VALUES (1, 0);
CREATE TRIGGER IF NOT EXISTS storage_overview_image_insert AFTER INSERT ON images
BEGIN UPDATE storage_overview_state SET revision = revision + 1 WHERE id = 1; END;
CREATE TRIGGER IF NOT EXISTS storage_overview_image_update AFTER UPDATE OF record, thumbnail ON images
BEGIN UPDATE storage_overview_state SET revision = revision + 1 WHERE id = 1; END;
CREATE TRIGGER IF NOT EXISTS storage_overview_image_delete AFTER DELETE ON images
BEGIN UPDATE storage_overview_state SET revision = revision + 1 WHERE id = 1; END;
