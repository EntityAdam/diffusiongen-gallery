using System.Runtime.ExceptionServices;
using DbUp;
using DbUp.Sqlite.Helpers;
using Microsoft.Data.Sqlite;

namespace Gallery.Migrations;

public static class GalleryDatabase
{
    public static void Upgrade(SqliteConnection connection)
    {
        var upgrader = DeployChanges.To
            .SqliteDatabase(new SharedConnection(connection))
            .WithScriptsAndCodeEmbeddedInAssembly(typeof(GalleryDatabase).Assembly)
            // Microsoft.Data.Sqlite starts an immediate transaction before DbUp reads its journal.
            .WithTransaction()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            ExceptionDispatchInfo.Capture(result.Error).Throw();
    }
}
