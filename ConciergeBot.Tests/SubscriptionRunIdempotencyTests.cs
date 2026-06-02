using ConciergeBot.Api.Data;
using ConciergeBot.Api.Models;
using Xunit;

namespace ConciergeBot.Tests;

// Audit (2026-05-30 #X1 / P59): InsertPendingAsync must be idempotent on the
// UNIQUE(subscription_id, tick_number) key. A crash between the pending-run insert
// and the subscription advance otherwise left the same (sub,tick) due, and a plain
// re-INSERT threw SQLITE_CONSTRAINT -> the worker's catch-and-continue abandoned the
// tick -> the subscription stuck failing forever. This is the boilerplate SOURCE of
// the LiquidGuard H4 bug; the fix here propagates to every future clone.
public class SubscriptionRunIdempotencyTests
{
    private static async Task SeedSub(SubscriptionRepository repo, string id)
        => await repo.InsertAsync(new Subscription(
            id, $"job-{id}", "0xbuyer", "tick_echo", "{}", "https://buyer.example/cb", "secret",
            60, 10, 0, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), null,
            DateTime.UtcNow.AddSeconds(60), "active", 0,
            PushMode: "webhook", StreamChainId: null, StreamJobId: null));

    [Fact]
    public async Task InsertPending_is_idempotent_on_duplicate_sub_tick()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        var subs = new SubscriptionRepository(t.Db);
        var runs = new SubscriptionRunRepository(t.Db);
        await SeedSub(subs, "s1");

        var id1 = await runs.InsertPendingAsync("s1", 1, DateTime.UtcNow, "{}");
        // Re-processing the same (sub, tick) after a crash must NOT throw and must
        // re-use the existing pending row so the tick can progress + advance the sub.
        var id2 = await runs.InsertPendingAsync("s1", 1, DateTime.UtcNow, "{}");

        Assert.True(id1 > 0);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Distinct_ticks_get_distinct_run_ids()
    {
        await using var t = TestDb.New();
        await t.Db.InitializeSchemaAsync();
        var subs = new SubscriptionRepository(t.Db);
        var runs = new SubscriptionRunRepository(t.Db);
        await SeedSub(subs, "s1");

        var id1 = await runs.InsertPendingAsync("s1", 1, DateTime.UtcNow, "{}");
        var id2 = await runs.InsertPendingAsync("s1", 2, DateTime.UtcNow, "{}");

        Assert.NotEqual(id1, id2);
    }
}
