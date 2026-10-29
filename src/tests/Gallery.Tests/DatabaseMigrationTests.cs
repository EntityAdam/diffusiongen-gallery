using System.Data;
using Gallery.Migrations;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class DatabaseMigrationTests
{
    [Fact]
    public void Upgrade_WhenDatabaseIsEmpty_ThenCreatesSchemaAndJournalsMigrationsOnce()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        GalleryDatabase.Upgrade(connection);
        GalleryDatabase.Upgrade(connection);

        Assert.Equal(ConnectionState.Open, connection.State);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;
            """;
        using (var reader = command.ExecuteReader())
        {
            var tables = new List<string>();
            while (reader.Read()) tables.Add(reader.GetString(0));
            Assert.Equal(["SchemaVersions", "drive_operations", "images", "media_index", "settings", "storage_overview_state"], tables);
        }
        AssertJournal(connection);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name IN (
                'images_fingerprint', 'drive_operations_kind_target', 'drive_operations_due', 'media_index_directory');
            """;
        Assert.Equal(4L, command.ExecuteScalar());
        command.CommandText = """
            INSERT INTO images (id, record, thumbnail) VALUES ('image', X'01', X'02');
            UPDATE images SET record = X'03' WHERE id = 'image';
            DELETE FROM images WHERE id = 'image';
            SELECT revision FROM storage_overview_state WHERE id = 1;
            """;
        Assert.Equal(3L, command.ExecuteScalar());
    }

    [Fact]
    public void Upgrade_WhenCurrentCatalogHasNoJournal_ThenAdoptsSchemaWithoutChangingData()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        GalleryDatabase.Upgrade(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE SchemaVersions;
            INSERT INTO images (id, record, thumbnail, fingerprint) VALUES ('image', X'01', X'02', X'03');
            INSERT INTO settings (id, payload) VALUES ('setting', X'04');
            INSERT INTO drive_operations (kind, target, image_id, payload, attempts, revision)
                VALUES ('operation', X'05', 'image', X'06', 2, 7);
            INSERT INTO media_index (target, directory, length) VALUES (X'05', X'07', 123);
            UPDATE storage_overview_state SET revision = 42 WHERE id = 1;
            """;
        command.ExecuteNonQuery();

        GalleryDatabase.Upgrade(connection);

        AssertJournal(connection);
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM images WHERE id = 'image' AND record = X'01' AND thumbnail = X'02' AND fingerprint = X'03'),
                (SELECT COUNT(*) FROM settings WHERE id = 'setting' AND payload = X'04'),
                (SELECT COUNT(*) FROM drive_operations WHERE sequence = 1 AND kind = 'operation' AND target = X'05'
                    AND image_id = 'image' AND payload = X'06' AND attempts = 2 AND revision = 7 AND state = 'pending'),
                (SELECT COUNT(*) FROM media_index WHERE target = X'05' AND directory = X'07' AND length = 123),
                (SELECT revision FROM storage_overview_state WHERE id = 1);
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        for (var column = 0; column < 4; column++)
            Assert.Equal(1L, reader.GetInt64(column));
        Assert.Equal(42L, reader.GetInt64(4));
    }

    [Fact]
    public void Upgrade_WhenExistingFingerprintsConflict_ThenRollsBackSchemaAndJournalAndCanRetry()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE images (id TEXT PRIMARY KEY, record BLOB NOT NULL, thumbnail BLOB NOT NULL, fingerprint BLOB);
            INSERT INTO images VALUES ('one', X'01', X'02', X'03'), ('two', X'04', X'05', X'03');
            """;
        command.ExecuteNonQuery();

        Assert.Throws<SqliteException>(() => GalleryDatabase.Upgrade(connection));

        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('settings', 'SchemaVersions', 'images_fingerprint')";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM images WHERE fingerprint = X'03'";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "UPDATE images SET fingerprint = NULL WHERE id = 'two'";
        command.ExecuteNonQuery();

        GalleryDatabase.Upgrade(connection);

        AssertJournal(connection);
        command.CommandText = "SELECT COUNT(*) FROM images";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData("WAL")]
    [InlineData("DELETE")]
    public async Task Upgrade_WhenIndependentConnectionsRace_ThenSerializesJournalAndLegacySchemaChanges(string journalMode)
    {
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "gallery.db"),
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode={journalMode};
            CREATE TABLE images (id TEXT PRIMARY KEY, record BLOB NOT NULL, thumbnail BLOB NOT NULL);
            INSERT INTO images VALUES ('legacy', X'01', X'02');
            """;
        command.ExecuteNonQuery();
        using var barrier = new Barrier(7);
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Factory.StartNew(() =>
        {
            using var independentConnection = new SqliteConnection(connectionString);
            independentConnection.Open();
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Migration workers did not reach the start barrier.");
            GalleryDatabase.Upgrade(independentConnection);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Migration workers did not reach the start barrier.");
        await Task.WhenAll(tasks);

        AssertJournal(connection);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'images_fingerprint'";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM images WHERE id = 'legacy' AND record = X'01' AND thumbnail = X'02' AND fingerprint IS NULL";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    private static void AssertJournal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ScriptName FROM SchemaVersions ORDER BY ScriptName";
        using var reader = command.ExecuteReader();
        var scripts = new List<string>();
        while (reader.Read()) scripts.Add(reader.GetString(0));
        Assert.Equal([
            "Gallery.Migrations.Scripts.M0001_Baseline.sql",
            "Gallery.Migrations.Scripts.M0002_Fingerprint.cs"
        ], scripts);
    }
}
