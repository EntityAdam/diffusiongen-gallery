CREATE TABLE IF NOT EXISTS vision_analysis (
    image_id TEXT PRIMARY KEY,
    payload BLOB NOT NULL
);
CREATE TRIGGER IF NOT EXISTS vision_analysis_image_delete AFTER DELETE ON images
BEGIN DELETE FROM vision_analysis WHERE image_id = OLD.id; END;
