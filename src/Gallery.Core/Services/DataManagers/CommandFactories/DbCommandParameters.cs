using System.Data;
using System.Data.Common;

namespace Gallery.Core.Services.DataManagers.CommandFactories;

internal static class DbCommandParameters
{
    public static DbParameter AddParameter(this DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    public static DbParameter AddParameter(this DbCommand command, string name, DbType type, int size = 0)
    {
        var parameter = command.AddParameter(name, DBNull.Value);
        parameter.DbType = type;
        if (size > 0) parameter.Size = size;
        return parameter;
    }
}
