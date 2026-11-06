using System.Data;
using DbUp.Engine;

namespace Gallery.Migrations.Turso.Scripts;

public sealed class M0002_CollectionPlacement : IScript
{
    public string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        using var command = dbCommandFactory();
        command.CommandText = "PRAGMA table_info(media)";
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader()) while (reader.Read()) existing.Add(reader.GetString(1));
        // A catalog restored without its journal may already have these additive columns.
        return string.Join("\n-- next\n", new[]
        {
            ("set_id", "ALTER TABLE media ADD COLUMN set_id TEXT;"),
            ("set_order", "ALTER TABLE media ADD COLUMN set_order INTEGER NOT NULL DEFAULT 0;"),
            ("legacy_collections", "ALTER TABLE media ADD COLUMN legacy_collections TEXT NOT NULL DEFAULT '[]';")
        }.Where(column => !existing.Contains(column.Item1)).Select(column => column.Item2));
    }
}
