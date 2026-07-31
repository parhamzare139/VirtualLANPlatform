using Microsoft.Data.Sqlite;
using VirtualLANPlatform.Core.Room;

namespace VirtualLANPlatform.Core.Storage;

/// <summary>Persists Room and Member records to SQLite.</summary>
public sealed class RoomRepository(DatabaseManager db)
{
    // ── Rooms ─────────────────────────────────────────────────────────────────

    public void SaveRoom(string roomId, string hostUsername, int localPort)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO rooms (id, created_at, host_username, local_port)
            VALUES ($id, $ts, $u, $p);";
        cmd.Parameters.AddWithValue("$id", roomId);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u",  hostUsername);
        cmd.Parameters.AddWithValue("$p",  localPort);
        cmd.ExecuteNonQuery();
    }

    // ── Members ───────────────────────────────────────────────────────────────

    public void RecordJoin(string roomId, MemberRecord m)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO room_members (room_id, username, virtual_ip, joined_at)
            VALUES ($rid, $u, $vip, $ts);";
        cmd.Parameters.AddWithValue("$rid", roomId);
        cmd.Parameters.AddWithValue("$u",   m.Username);
        cmd.Parameters.AddWithValue("$vip", m.VirtualIP);
        cmd.Parameters.AddWithValue("$ts",  new DateTimeOffset(m.JoinedAt).ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void RecordLeave(string roomId, string virtualIP)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            UPDATE room_members
            SET left_at = $ts
            WHERE room_id = $rid AND virtual_ip = $vip AND left_at IS NULL;";
        cmd.Parameters.AddWithValue("$ts",  DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$rid", roomId);
        cmd.Parameters.AddWithValue("$vip", virtualIP);
        cmd.ExecuteNonQuery();
    }

    // ── History query ─────────────────────────────────────────────────────────

    /// <summary>Returns the 20 most recent rooms, newest first.</summary>
    public List<(string Id, DateTime CreatedAt, string HostUsername)> GetRecentRooms()
    {
        var list = new List<(string, DateTime, string)>();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT id, created_at, host_username FROM rooms ORDER BY created_at DESC LIMIT 20;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).UtcDateTime;
            list.Add((reader.GetString(0), ts, reader.GetString(2)));
        }
        return list;
    }
}
