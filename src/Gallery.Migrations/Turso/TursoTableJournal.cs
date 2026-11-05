using System.Data;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Sqlite;

namespace Gallery.Migrations.Turso;

internal sealed class TursoTableJournal(Func<IConnectionManager> connections, Func<IUpgradeLog> log)
    : SqliteTableJournal(connections, log, TursoGalleryDatabase.JournalTableName)
{
    // AUTOINCREMENT creates a separate native sequence table. Avoid introducing that table into
    // legacy logical backup fingerprints; script names already provide a stable unique key.
    protected override string CreateSchemaTableSql(string quotedPrimaryKeyName) =>
        $"CREATE TABLE {FqSchemaTableName} (ScriptName TEXT PRIMARY KEY NOT NULL, Applied TEXT NOT NULL) STRICT";

    protected override IDbCommand GetInsertScriptCommand(Func<IDbCommand> commands, SqlScript script)
    {
        var command = base.GetInsertScriptCommand(commands, script);
        // The native Turso binder requires parameter names to include their SQL prefix.
        foreach (IDataParameter parameter in command.Parameters)
            parameter.ParameterName = "@" + parameter.ParameterName;
        return command;
    }
}
