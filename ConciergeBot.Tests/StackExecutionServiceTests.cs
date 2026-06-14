using System.Net;
using System.Text;
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

    // Audit XB-2: SafeRoute returns HTTP 200 even for a BLOCK verdict. The pre-swap
    // safety leg must GATE on the verdict, not just the HTTP status — a BLOCK must
    // NOT count toward okCount / overall "complete".
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string _json;
        public StubHttpClientFactory(string json) => _json = json;
        public HttpClient CreateClient(string name)
            => new(new StubHandler(_json)) { BaseAddress = new Uri("http://saferoutebot-api:5000/") };
    }

    private static StackExecutionRequest SwapReq()
        => new(
            "swap safely",
            new[] { new StackRecommendation("TheSafeRouteBot", "safe_quote", null,
                new Dictionary<string, object?>
                {
                    ["sellToken"] = "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913",
                    ["buyToken"]  = "0x4ed4e862860bed51a9570b96d89af5e1b0efefed",
                    ["amountIn"]  = "1000000",
                }) },
            "0x00000000000000000000000000000000000a11ce",
            new[] { "base" },
            null);

    [Fact]
    public async Task SafeRouteBot_BlockVerdict_IsNotCountedOk()
    {
        const string blockBody = """{"verdict":"BLOCK","reasons":[{"check":"sell_tax","status":"BLOCK","detail":"sell_reverted"}],"route":null,"validUntil":"2026-06-14T00:00:00Z"}""";
        var svc = new StackExecutionService(
            new StubHttpClientFactory(blockBody),
            Cfg(("PortfolioRun:SafeRouteBotApiKey", "testkey")),
            NullLogger<StackExecutionService>.Instance);

        var result = await svc.ExecuteAsync(SwapReq(), CancellationToken.None);

        var hire = Assert.Single(result.ExecutedHires);
        Assert.NotEqual("ok", hire.Status);                         // a BLOCK verdict is not "ok"
        Assert.Contains("BLOCK", hire.Status, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("complete", result.OverallStatus);          // overall must not read complete
    }

    [Fact]
    public async Task SafeRouteBot_CautionVerdict_IsOk()
    {
        const string cautionBody = """{"verdict":"CAUTION","reasons":[],"route":{"target":"0xr"},"validUntil":"2026-06-14T00:00:00Z"}""";
        var svc = new StackExecutionService(
            new StubHttpClientFactory(cautionBody),
            Cfg(("PortfolioRun:SafeRouteBotApiKey", "testkey")),
            NullLogger<StackExecutionService>.Instance);

        var result = await svc.ExecuteAsync(SwapReq(), CancellationToken.None);

        var hire = Assert.Single(result.ExecutedHires);
        Assert.Equal("ok", hire.Status);                            // CAUTION still completes (route provided, buyer warned)
    }
}
