using Microsoft.Data.Sqlite;

namespace SDemo.App.Meeting.Storage;

/// <summary>数据库连接与路径。schema 迁移在 <see cref="MeetingSchema"/>。</summary>
public sealed class MeetingDatabase : IDisposable
{
    private readonly string _connectionString;

    private MeetingDatabase(string path, string connectionString)
    {
        Path = path;
        _connectionString = connectionString;
    }

    public string Path { get; }

    public static MeetingDatabase Open(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();

        var db = new MeetingDatabase(path, cs);
        using var conn = db.Connect();
        MeetingSchema.Migrate(conn);
        return db;
    }

    public SqliteConnection Connect()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Dispose()
    {
        // 连接池已在字符串里关掉，这里不需要显式清理；
        // 但测试里删临时库前必须调用 SqliteConnection.ClearAllPools()。
    }
}
