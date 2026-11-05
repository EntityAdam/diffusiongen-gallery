using DbUp.Sqlite;
using DbUp.Sqlite.Helpers;

namespace Gallery.Migrations.Turso;

// The adapter supplies the open encrypted connection; DbUp must not create or close another one.
internal sealed class TursoConnectionManager(SharedConnection connection) : SqliteConnectionManager(connection)
{
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents) =>
        // Turso 0.8.3 prepares one statement per command. Explicit standalone boundaries also keep
        // trigger bodies intact; splitting at semicolons would split their BEGIN/END statements.
        System.Text.RegularExpressions.Regex.Split(scriptContents, @"^\s*-- next\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(statement => statement.Trim()).Where(statement => statement.Length > 0);
}
