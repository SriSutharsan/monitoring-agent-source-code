using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Agent.Storage;

/// <summary>The only provider-specific code in the storage layer.</summary>
public static class SqliteConnectionFactory
{
    public static System.Func<DbConnection> Create(string databasePath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false          // connections are short-lived; avoids file locks lingering during upgrades/uninstall
        }.ToString();
        return () => new SqliteConnection(cs);
    }
}
