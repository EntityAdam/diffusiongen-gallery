using Gallery.Core.Services.DataManagers;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

// Persistence tests require independent connections to verify database-level migration ownership.
[Trait("Category", "acceptance")]
public sealed class SchemaConcurrencyTests
{
    [Fact]
    public async Task Initialize_WhenIndependentStoresUpgradeOneLegacyCatalog_ThenAllCompleteWithOneFingerprintColumnAndIndex()
    {
        // Arrange
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var databasePath = Path.Combine(fixture.Root, "gallery.db");
        await using var sqliteConnection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await sqliteConnection.OpenAsync();
        using var arrangeCommand = sqliteConnection.CreateCommand();
        arrangeCommand.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE images (id TEXT PRIMARY KEY, record BLOB NOT NULL, thumbnail BLOB NOT NULL);
            INSERT INTO images (id, record, thumbnail) VALUES ('legacy', X'0102', X'0304');
            """;
        await arrangeCommand.ExecuteNonQueryAsync();
        GalleryStore[] galleryStores = Enumerable.Range(0, 6).Select(_ => new GalleryStore(databasePath)).ToArray();
        using var barrier = new Barrier(galleryStores.Length + 1);

        // Act
        Task[] initializationTasks = galleryStores.Select(galleryStore => Task.Factory.StartNew(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Concurrent schema initialization did not reach its start barrier.");
            await galleryStore.InitializeAsync();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Concurrent schema initialization did not reach its start barrier.");
        await Task.WhenAll(initializationTasks);

        // Assert
        using var verifyCommand = sqliteConnection.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'),
                (SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'images_fingerprint'),
                (SELECT COUNT(*) FROM images WHERE id = 'legacy' AND record = X'0102' AND thumbnail = X'0304');
            """;
        await using var sqliteDataReader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await sqliteDataReader.ReadAsync());
        Assert.Equal(1L, sqliteDataReader.GetInt64(0));
        Assert.Equal(1L, sqliteDataReader.GetInt64(1));
        Assert.Equal(1L, sqliteDataReader.GetInt64(2));
    }

    [Fact]
    public async Task Initialize_WhenFingerprintIndexCannotBeCreated_ThenRollsBackTheWholeSchemaUpgradeAndCanRetry()
    {
        // Arrange
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var databasePath = Path.Combine(fixture.Root, "gallery.db");
        await using var sqliteConnection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await sqliteConnection.OpenAsync();
        using var arrangeCommand = sqliteConnection.CreateCommand();
        arrangeCommand.CommandText = """
            CREATE TABLE images (id TEXT PRIMARY KEY, record BLOB NOT NULL, thumbnail BLOB NOT NULL);
            CREATE TABLE images_fingerprint (unexpected TEXT);
            """;
        await arrangeCommand.ExecuteNonQueryAsync();
        var galleryStore = new GalleryStore(databasePath);

        // Act
        var initializationError = await Record.ExceptionAsync(galleryStore.InitializeAsync);

        // Assert
        Assert.IsType<SqliteException>(initializationError);
        using var verifyCommand = sqliteConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
        Assert.Equal(0L, await verifyCommand.ExecuteScalarAsync());
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'settings'";
        Assert.Equal(0L, await verifyCommand.ExecuteScalarAsync());

        // Arrange
        arrangeCommand.CommandText = "DROP TABLE images_fingerprint";
        await arrangeCommand.ExecuteNonQueryAsync();

        // Act
        await galleryStore.InitializeAsync();

        // Assert
        verifyCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
        Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'images_fingerprint'";
        Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
    }
}
