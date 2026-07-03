using ConciergeBot.Api.Models;
using ConciergeBot.Api.Services;
using Microsoft.Data.Sqlite;

namespace ConciergeBot.Api.Data;

public class SubscriptionRepository
{
    private const int SuspendThreshold = 3;
    private readonly Db _db;
    private readonly WebhookSecretCipher _cipher;

    // The cipher is opt-in via WEBHOOK_SECRET_ENCRYPTION_KEY. When the key is
    // unset, Protect/Unprotect are no-ops and webhook_secret stays plaintext.
    // Program.cs fails fast at boot in non-Development unless an operator has
    // explicitly opted into plaintext via CONCIERGEBOT_ALLOW_PLAINTEXT_WEBHOOK_SECRETS=true.
    // Lazy migration: old plaintext rows decrypt as-is via the "v1:" prefix
    // sniff in Unprotect — they only become ciphertext on the next write.
    public SubscriptionRepository(Db db, WebhookSecretCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    // Backward-compatible overload for any existing test that constructs the
    // repository directly without a cipher. Tests get the no-key (no-op) cipher,
    // which matches the pre-cipher behaviour bit-for-bit.
    public SubscriptionRepository(Db db)
        : this(db, new WebhookSecretCipher(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()))
    { }

    public async Task InsertAsync(Subscription s)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        BindInsertCommand(cmd, s);
        await cmd.ExecuteNonQueryAsync();
    }

    // Audit (2026-07 #4): atomic quota-gated insert. The active-subscription
    // count and the INSERT MUST run in one transaction, or two concurrent
    // create-subscription calls can both pass the COUNT before either commits
    // and blow past the cap (a TOCTOU the previous check-then-insert in
    // SubscriptionService had). BEGIN IMMEDIATE (deferred:false) takes the write
    // lock up front, so the COUNTs and the INSERT are serialized against every
    // other writer on the DB (busy_timeout=5000 makes a contending writer WAIT,
    // not fail). Enforced here -- where the check and the write share the txn --
    // rather than in the service. 0/negative cap = unlimited (guarded per-cap).
    // Throws SubscriptionLimitException (-> HTTP 429) on a hit; nothing is
    // inserted (the throw disposes the uncommitted transaction = rollback).
    public async Task InsertWithQuotaAsync(Subscription s, int maxActiveGlobal, int maxActivePerBuyer)
    {
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction(deferred: false);

        if (maxActiveGlobal > 0)
        {
            await using var count = conn.CreateCommand();
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM subscriptions WHERE status='active'";
            if (Convert.ToInt32(await count.ExecuteScalarAsync()) >= maxActiveGlobal)
                throw new SubscriptionLimitException("global active-subscription limit reached");
        }
        if (maxActivePerBuyer > 0 && !string.IsNullOrEmpty(s.BuyerAgent))
        {
            await using var count = conn.CreateCommand();
            count.Transaction = tx;
            // COLLATE NOCASE so a buyer can't bypass the per-buyer cap by varying
            // address case (matches CountActiveByBuyerAsync + portfolio convention).
            count.CommandText =
                "SELECT COUNT(*) FROM subscriptions WHERE status='active' AND buyer_agent = $b COLLATE NOCASE";
            count.Parameters.AddWithValue("$b", s.BuyerAgent);
            if (Convert.ToInt32(await count.ExecuteScalarAsync()) >= maxActivePerBuyer)
                throw new SubscriptionLimitException("per-buyer active-subscription limit reached");
        }

        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            BindInsertCommand(ins, s);
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    // Shared INSERT binding for InsertAsync + InsertWithQuotaAsync so the column
    // list and the cipher-wrapping of webhook_secret never diverge between the
    // two write paths. Caller sets cmd.Transaction (if any) and executes.
    private void BindInsertCommand(SqliteCommand cmd, Subscription s)
    {
        cmd.CommandText = @"
            INSERT INTO subscriptions
                (id, job_id, buyer_agent, offering_name, requirement_json,
                 webhook_url, webhook_secret, interval_seconds, ticks_purchased,
                 ticks_delivered, created_at, expires_at, last_run_at, next_run_at,
                 status, consecutive_failures, push_mode, stream_chain_id, stream_job_id)
            VALUES ($id, $job, $buyer, $off, $req,
                    $url, $sec, $iv, $tp,
                    $td, $ca, $ea, $lra, $nra,
                    $st, $cf, $pm, $sci, $sji);";
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$job", s.JobId);
        cmd.Parameters.AddWithValue("$buyer", s.BuyerAgent);
        cmd.Parameters.AddWithValue("$off", s.OfferingName);
        cmd.Parameters.AddWithValue("$req", s.RequirementJson);
        // webhook_url / webhook_secret are NOT NULL on disk (legacy schema).
        // For inJobStream subs, persist empty strings instead of null so the
        // constraint holds; SubscriptionRepository.Read projects empty → null
        // so callers see Subscription.WebhookUrl as nullable.
        cmd.Parameters.AddWithValue("$url", s.WebhookUrl ?? string.Empty);
        // Wrap webhook_secret through the cipher BEFORE persist. No-op when
        // WEBHOOK_SECRET_ENCRYPTION_KEY is unset; otherwise produces the
        // "v1:iv.tag.ct" envelope on disk. inJobStream rows hold "" which
        // Protect short-circuits to "" — the column stays NOT NULL on disk.
        cmd.Parameters.AddWithValue("$sec", _cipher.Protect(s.WebhookSecret ?? string.Empty));
        cmd.Parameters.AddWithValue("$iv", s.IntervalSeconds);
        cmd.Parameters.AddWithValue("$tp", s.TicksPurchased);
        cmd.Parameters.AddWithValue("$td", s.TicksDelivered);
        cmd.Parameters.AddWithValue("$ca", s.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ea", s.ExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lra", (object?)s.LastRunAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nra", s.NextRunAt.ToString("O"));
        cmd.Parameters.AddWithValue("$st", s.Status);
        cmd.Parameters.AddWithValue("$cf", s.ConsecutiveFailures);
        cmd.Parameters.AddWithValue("$pm", s.PushMode);
        cmd.Parameters.AddWithValue("$sci", (object?)s.StreamChainId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sji", (object?)s.StreamJobId ?? DBNull.Value);
    }

    // Audit (2026-05-30 #M3 / P60): active-subscription quota inputs. Each active
    // row is re-selected every 10s poll + fans out compute + an outbound webhook
    // POST, so unbounded creation linearly grows standing worker CPU + outbound
    // HTTP + DB rows on a single-instance / shared-droplet model. Authoritative
    // enforcement is now atomic in InsertWithQuotaAsync (2026-07 #4); these
    // standalone COUNTs stay as cheap read helpers (ix on status already exists).
    public async Task<int> CountActiveAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM subscriptions WHERE status='active'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> CountActiveByBuyerAsync(string buyerAgent)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        // COLLATE NOCASE so a buyer can't bypass the per-buyer cap by varying
        // address case (matches the portfolio address-normalisation convention).
        cmd.CommandText = "SELECT COUNT(*) FROM subscriptions WHERE status='active' AND buyer_agent = $b COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$b", buyerAgent ?? string.Empty);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<Subscription?> GetByIdAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM subscriptions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRow(reader) : null;
    }

    public async Task<List<Subscription>> GetDueAsync(DateTime now, int limit)
    {
        var rows = new List<Subscription>();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM subscriptions
            WHERE status='active' AND next_run_at <= $now
            ORDER BY next_run_at LIMIT $limit";
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(ReadRow(reader));
        return rows;
    }

    public async Task RecordTickResultAsync(
        string id,
        bool succeeded,
        DateTime lastRunAt,
        DateTime nextRunAt,
        bool completedSubscription)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        if (succeeded)
        {
            cmd.CommandText = @"
                UPDATE subscriptions
                SET ticks_delivered = ticks_delivered + 1,
                    last_run_at     = $lra,
                    next_run_at     = $nra,
                    consecutive_failures = 0,
                    status = CASE WHEN $done = 1 THEN 'completed' ELSE 'active' END
                WHERE id = $id";
        }
        else
        {
            cmd.CommandText = $@"
                UPDATE subscriptions
                SET ticks_delivered = ticks_delivered + 1,
                    last_run_at     = $lra,
                    next_run_at     = $nra,
                    consecutive_failures = consecutive_failures + 1,
                    status = CASE
                        WHEN consecutive_failures + 1 >= {SuspendThreshold} THEN 'suspended'
                        WHEN $done = 1 THEN 'completed'
                        ELSE 'active'
                    END
                WHERE id = $id";
        }
        cmd.Parameters.AddWithValue("$lra", lastRunAt.ToString("O"));
        cmd.Parameters.AddWithValue("$nra", nextRunAt.ToString("O"));
        cmd.Parameters.AddWithValue("$done", completedSubscription ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ResetConsecutiveFailuresAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE subscriptions SET consecutive_failures = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private Subscription ReadRow(SqliteDataReader r)
    {
        var url = r.GetString(r.GetOrdinal("webhook_url"));
        var rawSec = r.GetString(r.GetOrdinal("webhook_secret"));
        // Unwrap through the cipher. Plaintext rows (no "v1:" prefix) pass
        // through unchanged — lazy migration: an old row only gets re-encrypted
        // when its parent subscription is re-inserted (which the boilerplate
        // never does post-create; downstream clones that ALTER rows must
        // round-trip the secret through Protect themselves).
        var sec = _cipher.Unprotect(rawSec);
        return new Subscription(
            Id: r.GetString(r.GetOrdinal("id")),
            JobId: r.GetString(r.GetOrdinal("job_id")),
            BuyerAgent: r.GetString(r.GetOrdinal("buyer_agent")),
            OfferingName: r.GetString(r.GetOrdinal("offering_name")),
            RequirementJson: r.GetString(r.GetOrdinal("requirement_json")),
            // Empty strings on disk represent NULL semantically — inJobStream subs
            // store empty to satisfy the legacy NOT NULL constraint.
            WebhookUrl: string.IsNullOrEmpty(url) ? null : url,
            WebhookSecret: string.IsNullOrEmpty(sec) ? null : sec,
            IntervalSeconds: r.GetInt32(r.GetOrdinal("interval_seconds")),
            TicksPurchased: r.GetInt32(r.GetOrdinal("ticks_purchased")),
            TicksDelivered: r.GetInt32(r.GetOrdinal("ticks_delivered")),
            CreatedAt: DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))).ToUniversalTime(),
            ExpiresAt: DateTime.Parse(r.GetString(r.GetOrdinal("expires_at"))).ToUniversalTime(),
            LastRunAt: r.IsDBNull(r.GetOrdinal("last_run_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("last_run_at"))).ToUniversalTime(),
            NextRunAt: DateTime.Parse(r.GetString(r.GetOrdinal("next_run_at"))).ToUniversalTime(),
            Status: r.GetString(r.GetOrdinal("status")),
            ConsecutiveFailures: r.GetInt32(r.GetOrdinal("consecutive_failures")),
            PushMode: r.GetString(r.GetOrdinal("push_mode")),
            StreamChainId: r.IsDBNull(r.GetOrdinal("stream_chain_id")) ? null : r.GetInt32(r.GetOrdinal("stream_chain_id")),
            StreamJobId: r.IsDBNull(r.GetOrdinal("stream_job_id")) ? null : r.GetString(r.GetOrdinal("stream_job_id"))
        );
    }
}
