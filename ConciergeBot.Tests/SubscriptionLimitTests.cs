using ConciergeBot.Api.Data;
using ConciergeBot.Api.Models;
using ConciergeBot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ConciergeBot.Tests;

// Audit (2026-05-30 #M3 / P60): the boilerplate enforces a configurable
// active-subscription quota (global + per-buyer) BEFORE insert, throwing
// SubscriptionLimitException (-> HTTP 429). These lock that, including the
// COLLATE NOCASE per-buyer bypass guard.
public class SubscriptionLimitTests
{
    private static SubscriptionService MakeSvc(TestDb t, Dictionary<string, string?> caps)
    {
        var dict = new Dictionary<string, string?> { ["ALLOW_INSECURE_WEBHOOKS"] = "true" };
        foreach (var kv in caps) dict[kv.Key] = kv.Value;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new SubscriptionService(new SubscriptionRepository(t.Db), new TickEchoRepository(t.Db), cfg);
    }

    private static CreateSubscriptionRequest Req(string jobId, string buyer) => new(
        JobId: jobId,
        BuyerAgent: buyer,
        OfferingName: "tick_echo",
        Requirement: new Dictionary<string, object>
        {
            ["ticks"] = 5,
            ["intervalSeconds"] = 3600,
            ["message"] = "hello",
            ["webhookUrl"] = "https://buyer.example.com/cb",
        },
        PushMode: "webhook");

    [Fact]
    public async Task Per_buyer_cap_blocks_the_n_plus_1th()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        var svc = MakeSvc(t, new() { ["Subscriptions:MaxActivePerBuyer"] = "2", ["Subscriptions:MaxActiveGlobal"] = "0" });

        await svc.CreateAsync(Req("job-1", "0xbuyerA"));
        await svc.CreateAsync(Req("job-2", "0xbuyerA"));
        await Assert.ThrowsAsync<SubscriptionLimitException>(() => svc.CreateAsync(Req("job-3", "0xbuyerA")));

        // a DIFFERENT buyer is unaffected by buyer A's cap
        await svc.CreateAsync(Req("job-4", "0xbuyerB"));
    }

    [Fact]
    public async Task Per_buyer_cap_is_case_insensitive()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        var svc = MakeSvc(t, new() { ["Subscriptions:MaxActivePerBuyer"] = "1", ["Subscriptions:MaxActiveGlobal"] = "0" });

        await svc.CreateAsync(Req("job-1", "0xABCDEF"));
        // same buyer, different case -> must still count against the cap
        await Assert.ThrowsAsync<SubscriptionLimitException>(() => svc.CreateAsync(Req("job-2", "0xabcdef")));
    }

    [Fact]
    public async Task Global_cap_blocks_the_n_plus_1th_across_buyers()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        var svc = MakeSvc(t, new() { ["Subscriptions:MaxActiveGlobal"] = "2", ["Subscriptions:MaxActivePerBuyer"] = "0" });

        await svc.CreateAsync(Req("job-1", "0xbuyerA"));
        await svc.CreateAsync(Req("job-2", "0xbuyerB"));
        await Assert.ThrowsAsync<SubscriptionLimitException>(() => svc.CreateAsync(Req("job-3", "0xbuyerC")));
    }

    [Fact]
    public async Task Zero_cap_means_unlimited()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        var svc = MakeSvc(t, new() { ["Subscriptions:MaxActiveGlobal"] = "0", ["Subscriptions:MaxActivePerBuyer"] = "0" });

        for (int i = 0; i < 12; i++)
            await svc.CreateAsync(Req($"job-{i}", "0xbuyerA")); // > default per-buyer 10, but 0 = unlimited
    }

    // Audit (2026-07 #4): the quota is enforced atomically inside
    // InsertWithQuotaAsync (one BEGIN IMMEDIATE txn per create), so a burst of
    // concurrent creates can never drive the active count past the cap. The old
    // check-then-insert in SubscriptionService was a TOCTOU that could exceed it
    // under exactly this race. We assert the upper-bound invariant (robust: it
    // holds regardless of how write-lock contention distributes successes).
    [Fact]
    public async Task Concurrent_creates_never_exceed_global_cap()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        const int cap = 10;
        var svc = MakeSvc(t, new() { ["Subscriptions:MaxActiveGlobal"] = cap.ToString(), ["Subscriptions:MaxActivePerBuyer"] = "0" });

        // Fire 4x the cap concurrently, each a distinct buyer/job.
        var tasks = Enumerable.Range(0, cap * 4).Select(async i =>
        {
            try { await svc.CreateAsync(Req($"job-c-{i}", $"0xbuyer{i}")); return true; }
            catch (SubscriptionLimitException) { return false; }  // capped -- expected
            catch (SqliteException)             { return false; }  // write-lock contention -- treat as not-created
        });
        var results = await Task.WhenAll(tasks);

        var active = await new SubscriptionRepository(t.Db).CountActiveAsync();
        Assert.True(active <= cap, $"active {active} exceeded cap {cap} under concurrency (TOCTOU regression)");
        Assert.True(active >= 1, "expected at least one concurrent create to succeed");
        Assert.True(results.Count(ok => ok) <= cap, "more creates reported success than the cap allows");
    }
}
