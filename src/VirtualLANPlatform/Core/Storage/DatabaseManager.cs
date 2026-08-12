using System.IO;
using Microsoft.Data.Sqlite;

namespace VirtualLANPlatform.Core.Storage;

/// <summary>
/// Opens the SQLite database and ensures the schema is up to date.
/// One instance per application lifetime — call Dispose on shutdown.
/// </summary>
public sealed class DatabaseManager : IDisposable
{
    private const string FileName = "vlp_data.db";

    private readonly SqliteConnection _conn;
    private bool _disposed;

    public SqliteConnection Connection => _conn;

    public DatabaseManager()
    {
        string dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VirtualLANPlatform", FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        // Improve write performance; acceptable for single-process use
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");

        ApplySchema();
    }

    private void ApplySchema()
    {
        Execute(@"
            CREATE TABLE IF NOT EXISTS rooms (
                id           TEXT    PRIMARY KEY,
                created_at   INTEGER NOT NULL,
                host_username TEXT   NOT NULL,
                local_port   INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS room_members (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id      TEXT    NOT NULL,
                username     TEXT    NOT NULL,
                joined_at    INTEGER NOT NULL,
                left_at      INTEGER,
                FOREIGN KEY (room_id) REFERENCES rooms(id)
            );
        ");
    }

    public void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public SqliteCommand CreateCommand() => _conn.CreateCommand();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }
}
