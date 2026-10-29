using System.Data;
using DbUp.Engine;

namespace Gallery.Migrations.Scripts;

public sealed class M0002_Fingerprint : IScript
{
    public string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        using var command = dbCommandFactory();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('images') WHERE name = 'fingerprint'";
        var hasFingerprint = Convert.ToInt64(command.ExecuteScalar()) != 0;
        return (hasFingerprint ? "" : "ALTER TABLE images ADD COLUMN fingerprint BLOB;")
            + "CREATE UNIQUE INDEX IF NOT EXISTS images_fingerprint ON images(fingerprint);";
    }
}
