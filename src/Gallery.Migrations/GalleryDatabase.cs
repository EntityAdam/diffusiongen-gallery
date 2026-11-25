using System.Runtime.ExceptionServices;
using DbUp;
using DbUp.Engine;
using DbUp.Sqlite.Helpers;
using Microsoft.Data.Sqlite;

namespace Gallery.Migrations;

public static class GalleryDatabase
{
    public static bool IsUpgradeRequired(SqliteConnection connection) =>
        CreateUpgrader(connection, transactional: false).IsUpgradeRequired();

    public static void Upgrade(SqliteConnection connection)
    {
        var result = CreateUpgrader(connection, transactional: true).PerformUpgrade();
        if (!result.Successful)
            ExceptionDispatchInfo.Capture(result.Error).Throw();
    }

    private static UpgradeEngine CreateUpgrader(SqliteConnection connection, bool transactional)
    {
        var builder = DeployChanges.To
            .SqliteDatabase(new SharedConnection(connection))
            .WithScriptsAndCodeEmbeddedInAssembly(typeof(GalleryDatabase).Assembly,
                name => name.StartsWith("Gallery.Migrations.Scripts.", StringComparison.Ordinal),
                name => name.StartsWith("Gallery.Migrations.Scripts.", StringComparison.Ordinal));
        if (transactional)
            builder.WithTransaction();
        return builder.Build();
    }
}
