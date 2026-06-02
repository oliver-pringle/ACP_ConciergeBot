using ConciergeBot.Api.Middleware;
using Xunit;

namespace ConciergeBot.Tests;

// Audit (2026-05-30 #X2 / P52): lock the rate-limiter's heavy-path coverage to the
// boilerplate's actual mapped routes. The boilerplate itself is correct, but this
// test SHIPS WITH THE BOILERPLATE so every clone inherits the drift-protection:
// a clone that renames a route (kebab vs underscore) or adds a heavy endpoint
// without extending HeavyPathPrefixes fails this test instead of silently
// un-throttling. (P52 has bitten RevokeBot + LiquidGuard via exactly those two
// sub-variants.) When cloning, update the InlineData lists to the clone's routes.
public class RateLimitCoverageTests
{
    [Theory]
    [InlineData("/subscriptions")]
    [InlineData("/subscriptions/abc123")]
    [InlineData("/echo")]
    [InlineData("/echo/abc123")]
    public void Heavy_write_routes_are_throttled(string path)
        => Assert.True(RateLimitMiddleware.IsHeavyPath(path), $"{path} is a heavy endpoint but is not rate-limited");

    [Theory]
    [InlineData("/health")]
    [InlineData("/v1/resources/echoStatus")]
    public void Free_routes_are_not_throttled(string path)
        => Assert.False(RateLimitMiddleware.IsHeavyPath(path));
}
