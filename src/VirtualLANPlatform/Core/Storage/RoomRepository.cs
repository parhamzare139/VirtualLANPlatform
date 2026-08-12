namespace VirtualLANPlatform.Core.Storage;

public sealed class RoomRepository(DatabaseManager db)
{
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

    public void RecordJoin(string roomId, string username)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO room_members (room_id, username, joined_at)
            VALUES ($rid, $u, $ts);";
        cmd.Parameters.AddWithValue("$rid", roomId);
        cmd.Parameters.AddWithValue("$u",   username);
        cmd.Parameters.AddWithValue("$ts",  DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void RecordLeave(string roomId, string username)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            UPDATE room_members SET left_at = $ts
            WHERE room_id = $rid AND username = $u AND left_at IS NULL;";
        cmd.Parameters.AddWithValue("$ts",  DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$rid", roomId);
        cmd.Parameters.AddWithValue("$u",   username);
        cmd.ExecuteNonQuery();
    }
}
