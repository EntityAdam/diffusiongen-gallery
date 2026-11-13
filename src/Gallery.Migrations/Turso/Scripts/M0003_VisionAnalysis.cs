using System.Data;
using DbUp.Engine;

namespace Gallery.Migrations.Turso.Scripts;

public sealed class M0003_VisionAnalysis : IScript
{
    public string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        using var command = dbCommandFactory();
        command.CommandText = "PRAGMA table_info(media)";
        var exists = false;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (reader.GetString(1) == "vision_facts") exists = true;
        return (exists ? "" : "ALTER TABLE media ADD COLUMN vision_facts TEXT;\n-- next\n") + """
            CREATE TABLE IF NOT EXISTS vision_analysis (
                image_id TEXT PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE,
                payload TEXT NOT NULL
            );
            """;
    }
}
