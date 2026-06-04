using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Data;

public class EchoRepository
{
    private readonly Db _db;
    public EchoRepository(Db db) => _db = db;

    public async Task<EchoRecord> InsertAsync(string message)
    {
        var receivedAt = DateTime.UtcNow;
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO echo_records (message, received_at) VALUES ($m, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$m", message);
        cmd.Parameters.AddWithValue("$t", receivedAt.ToString("O"));
        var id = (long)(await cmd.ExecuteScalarAsync())!;
        return new EchoRecord(id, message, receivedAt);
    }

    public async Task<EchoRecord?> GetAsync(long id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, message, received_at FROM echo_records WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new EchoRecord(reader.GetInt64(0), reader.GetString(1), DateTime.Parse(reader.GetString(2)).ToUniversalTime());
    }

    public async Task<(long Count, DateTime? LastReceivedAtUtc)> GetStatusAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(received_at) FROM echo_records;";
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (0, null);
        var count = reader.GetInt64(0);
        DateTime? lastAt = reader.IsDBNull(1)
            ? null
            : DateTime.Parse(reader.GetString(1)).ToUniversalTime();
        return (count, lastAt);
    }
}
