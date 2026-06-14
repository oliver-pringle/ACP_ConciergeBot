using ConciergeBot.Api.Models;
using ConciergeBot.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ConciergeBot.Tests;

// Round 19 P2 — SafeRouteBot wired into stack_execute as the pre-swap safety leg.
// These cover the two no-HTTP guard paths (so they're hermetic): it must be a
// RECOGNISED target (reports the key gap, not the "no internal API" unwired skip)
// and it must skip cleanly when the swap params are absent from requirementHint.
public class StackExecutionServiceTests
{
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException($"no HTTP expected for '{name}' in this path");
    }

    private static StackExecutionService Build(IConfiguration cfg)
        => new(new ThrowingHttpClientFactory(), cfg, NullLogger<StackExecutionService>.Instance);

    private static IConfiguration Cfg(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(x => x.Key, x => (string?)x.Value))
            .Build();

    private static StackExecutionRequest Req(Dictionary<string, object?>? hint)
        => new(
            "swap safely",
            new[] { new StackRecommendation("TheSafeRouteBot", "safe_quote", null, hint) },
            "0x00000000000000000000000000000000000a11ce",
            new[] { "base" },
            null);

    [Fact]
    public async Task SafeRouteBot_IsWired_NotSkippedAsUnsupported()
    {
        // No key configured -> the SafeRoute executor is REACHED and reports the key
        // gap, rather than the "no internal API available" skip used for unwired agents.
        var svc = Build(Cfg());
        var result = await svc.ExecuteAsync(Req(null), CancellationToken.None);

        var hire = Assert.Single(result.ExecutedHires);
        Assert.Equal("TheSafeRouteBot", hire.Agent);
        Assert.Contains("no API key", hire.Status);
        Assert.DoesNotContain(result.FailedHires, f => f.Reason.Contains("no internal API"));
    }

    [Fact]
    public async Task SafeRouteBot_MissingSwapParams_Skipped()
    {
        var svc = Build(Cfg(("PortfolioRun:SafeRouteBotApiKey", "testkey")));
        var result = await svc.ExecuteAsync(Req(null), CancellationToken.None);

        var hire = Assert.Single(result.ExecutedHires);
        Assert.Contains("no sellToken", hire.Status);
    }
}
