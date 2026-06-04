using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Data;

public class TickEchoRepository
{
    private readonly Db _db;
    public TickEchoRepository(Db db) => _db = db;

    public async Task InsertAsync(string subscriptionId, string message)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO tick_echo_state (subscription_id, message, created_at)
            VALUES ($s, $m, $c)";
        cmd.Parameters.AddWithValue("$s", subscriptionId);
        cmd.Parameters.AddWithValue("$m", message);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<TickEchoState?> GetAsync(string subscriptionId)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT subscription_id, message, created_at FROM tick_echo_state WHERE subscription_id=$s";
        cmd.Parameters.AddWithValue("$s", subscriptionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new TickEchoState(reader.GetString(0), reader.GetString(1), DateTime.Parse(reader.GetString(2)).ToUniversalTime());
    }
}
