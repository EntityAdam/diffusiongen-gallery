using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Gallery.Migrations.Turso;

namespace Gallery.Tests;

public sealed class TursoDatabaseMigrationTests
{
    private const string Baseline = "Gallery.Migrations.Turso.Scripts.M0001_Catalog.cs";

    [Fact]
    public void New_catalog_runs_only_Turso_migrations_and_journals_once()
    {
        using var fixture = new DatabaseFixture();
        TursoGalleryDatabase.Upgrade(fixture.Connection, allowCreate: true);
        TursoGalleryDatabase.Upgrade(fixture.Connection);

        Assert.Equal(ConnectionState.Open, fixture.Connection.State);
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT version FROM catalog_format WHERE id=1"));
        Assert.Equal(Baseline, Scalar(fixture.Connection, "SELECT ScriptName FROM turso_schema_versions"));
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM turso_schema_versions"));
        Assert.Equal(0L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('images','SchemaVersions')"));
        Assert.Equal(3L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'media_revision_%'"));
    }

    [Fact]
    public void Existing_version_one_catalog_is_adopted_without_recreating_or_changing_data()
    {
        using var fixture = new DatabaseFixture();
        CreateLegacyCatalog(fixture.Connection);
        Execute(fixture.Connection, "INSERT INTO settings VALUES('private-setting','{\"value\":42}')");
        Execute(fixture.Connection, "INSERT INTO drive_operations(kind,target,payload,attempts,revision) VALUES('delete_file',X'01',X'02',3,7)");
        Execute(fixture.Connection, "INSERT INTO media_index VALUES(X'03',X'04',123)");
        Execute(fixture.Connection, "UPDATE storage_overview_state SET revision=42 WHERE id=1");

        TursoGalleryDatabase.Upgrade(fixture.Connection);
        TursoGalleryDatabase.Upgrade(fixture.Connection);

        Assert.Equal("{\"value\":42}", Scalar(fixture.Connection, "SELECT payload FROM settings WHERE id='private-setting'"));
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM drive_operations WHERE sequence=1 AND attempts=3 AND revision=7 AND target=X'01' AND payload=X'02'"));
        Assert.Equal(123L, Scalar(fixture.Connection, "SELECT length FROM media_index WHERE target=X'03' AND directory=X'04'"));
        Assert.Equal(42L, Scalar(fixture.Connection, "SELECT revision FROM storage_overview_state WHERE id=1"));
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM turso_schema_versions"));
    }

    [Fact]
    public void Failure_late_in_baseline_rolls_back_schema_and_journal_and_allows_retry()
    {
        using var fixture = new DatabaseFixture();
        using var failing = new FailingConnection(fixture.Connection);
        Assert.Throws<InvalidOperationException>(() => TursoGalleryDatabase.Upgrade(failing, allowCreate: true));
        Assert.Equal(0L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'"));

        TursoGalleryDatabase.Upgrade(fixture.Connection, allowCreate: true);
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM turso_schema_versions"));
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='media_revision_delete'"));
    }

    [Fact]
    public void Unknown_format_is_rejected_before_schema_or_journal_changes()
    {
        using var fixture = new DatabaseFixture();
        CreateLegacyCatalog(fixture.Connection);
        Execute(fixture.Connection, "UPDATE catalog_format SET version=99 WHERE id=1");

        Assert.Throws<InvalidOperationException>(() => TursoGalleryDatabase.Upgrade(fixture.Connection));
        Assert.Equal(99L, Scalar(fixture.Connection, "SELECT version FROM catalog_format WHERE id=1"));
        Assert.Equal(0L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='turso_schema_versions'"));
    }

    [Fact]
    public void Reading_an_uninitialized_catalog_or_initializing_a_foreign_schema_is_rejected()
    {
        using var fixture = new DatabaseFixture();
        Assert.Throws<InvalidOperationException>(() => TursoGalleryDatabase.Upgrade(fixture.Connection));
        Assert.Equal(0L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'"));
        Execute(fixture.Connection, "CREATE TABLE images(id TEXT PRIMARY KEY,record BLOB,thumbnail BLOB)");
        Assert.Throws<InvalidOperationException>(() => TursoGalleryDatabase.Upgrade(fixture.Connection, allowCreate: true));
        Assert.Equal(1L, Scalar(fixture.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND type='table'"));
    }

    private static void CreateLegacyCatalog(DbConnection connection)
    {
        using var stream = typeof(TursoGalleryDatabase).Assembly.GetManifestResourceStream(
            "Gallery.Migrations.Turso.Schema.M0001_Catalog.sql")!;
        using var reader = new StreamReader(stream);
        foreach (var statement in reader.ReadToEnd().Split("-- next", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            Execute(connection, statement);
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "gallery-turso-migrations-" + Guid.NewGuid().ToString("N"));
        public DbConnection Connection { get; }
        public DatabaseFixture()
        {
            Directory.CreateDirectory(root);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var builder = new DbConnectionStringBuilder
                {
                    ["Data Source"] = Path.Combine(root, "catalog.db"), ["Pooling"] = false,
                    ["Encryption Cipher"] = "aes256gcm", ["Encryption Key"] = Convert.ToHexString(key)
                };
                Connection = new Turso.TursoConnection(builder.ConnectionString);
                Connection.Open();
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        public void Dispose()
        {
            Connection.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    // Inject a failure after real native DDL has executed, so rollback assertions exercise DbUp's
    // transaction and journal against the real engine rather than a mocked migration implementation.
    private sealed class FailingConnection(DbConnection inner) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void Open() => inner.Open();
        public override void Close() => inner.Close();
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new FailingCommand(inner.CreateCommand());
    }

    private sealed class FailingCommand(DbCommand inner) : DbCommand
    {
        [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        public override int ExecuteNonQuery() => CommandText.StartsWith("CREATE TRIGGER media_revision_delete", StringComparison.Ordinal)
            ? throw new InvalidOperationException("Injected migration failure.") : inner.ExecuteNonQuery();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
