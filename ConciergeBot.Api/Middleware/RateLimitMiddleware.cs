using System.Collections.Concurrent;

namespace ConciergeBot.Api.Middleware;

/// Two-bucket sliding-window rate limit on heavy / write endpoints. Closes
/// audit finding #9 ("no rate limiting"). Placed BEFORE the auth middleware
/// so unauthenticated floods are also throttled.
///
///   1. Per-X-API-Key bucket — 600 req/min default. Defends against a
///      runaway loop in a legitimate cross-bot consumer (the boilerplate
///      ships single-key only, but downstream bots cloning this often add
///      per-consumer keys).
///   2. Per-client-IP bucket — 60 req/min default. Defends against an
///      attacker who has stolen the API key but is still bound to one IP
///      per session.
///
/// Either bucket exceeding capacity yields a 429. The /health and
/// /v1/resources/* surfaces are exempt — they're meant for unauthenticated
/// liveness probes / orchestrator pre-flight introspection.
///
/// Ported from ACP_OracleBot v0.7 RateLimitMiddleware (commit a2d3731
/// 2026-05-24), which in turn descends from ACP_ChainlinkBot 2026-05-22.
/// Heavy-path list trimmed to what the boilerplate exposes — clones add
/// their own paths to HeavyPathPrefixes alongside their domain endpoints.
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;

    // /health per-IP burst shedding (audit P15/P19): a passive scanner's bounded
    // burst on the unauthenticated liveness endpoint trips a 429 (loopback exempt;
    // nothing internal polls /health on this bot). Self-contained: independent of the
    // heavy per-IP bucket above so it works regardless of that limiter's shape.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _healthBuckets = new();
    private const int HealthBurstCap = 3;
    private static readonly TimeSpan HealthBurstWindow = TimeSpan.FromSeconds(10);
    private readonly int             _apiKeyCapacity;
    private readonly int             _ipCapacity;
    private readonly TimeSpan        _window;

    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _apiKeyBuckets = new();
    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _ipBuckets     = new();

    private long _tickCounter;
    private const int EvictEveryNTicks = 256;
    // P15 hardening: hard cap on the per-X-API-Key bucket dictionary so an
    // attacker rotating random keys at high RPS can't balloon memory between
    // eviction sweeps. When the cap is hit, NEW unrecognised keys skip the per-
    // key reservation (per-IP still throttles). Eviction sweep returns the dict
    // to nominal once stale buckets pass `2 * window`.
    private const int ApiKeyBucketHardCap = 8192;

    // Path prefixes that count as "heavy" — write paths or compute fan-out.
    // /health and /v1/resources/* are excluded by NOT being listed here.
    // Clones add domain-specific prefixes alongside these.
    private static readonly string[] HeavyPathPrefixes =
    {
        "/subscriptions",   // POST creates + writes a row; GET hits SQLite
        "/echo",            // POST writes a row; GET reads
    };

    /// Audit (2026-05-30 #X2 / P52): public + static so a unit test can assert
    /// every heavy MapPost/MapGet route is covered by a prefix. The boilerplate's
    /// own routes ARE covered, but clones (RevokeBot, LiquidGuard) drifted — kebab
    /// vs underscore, or never extending this list as endpoints were added — and
    /// silently un-throttled their heavy routes. Shipping `IsHeavyPath` + a
    /// RateLimitCoverageTests in the boilerplate means every clone inherits the
    /// drift-protection: a heavy route with no matching prefix fails the build.
    /// (Beware StartsWith asymmetry: a more-specific prefix does NOT cover a
    /// shorter sibling — prefer the base stem.)
    public static bool IsHeavyPath(string path)
    {
        foreach (var prefix in HeavyPathPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public RateLimitMiddleware(RequestDelegate next, IConfiguration cfg)
    {
        _next = next;
        _apiKeyCapacity = cfg.GetValue("RateLimit:HeavyEndpointCapPerApiKey", 600);
        _ipCapacity     = cfg.GetValue("RateLimit:HeavyEndpointCapPerIp",      60);
        _window         = TimeSpan.FromMinutes(1);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        // /health burst shedding (P15/P19): loopback (internal probes) exempt; the 429
        // carries the every-response security headers since it short-circuits before
        // the later security-headers middleware would run.
        if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase)
            && ctx.Connection.RemoteIpAddress is { } hrip && !System.Net.IPAddress.IsLoopback(hrip))
        {
            var hnow = DateTime.UtcNow;
            var hb = _healthBuckets.AddOrUpdate(hrip.ToString(),
                _ => (hnow, 1),
                (_, b) => hnow - b.WindowStart > HealthBurstWindow ? (hnow, 1) : (b.WindowStart, b.Count + 1));
            if (hb.Count > HealthBurstCap)
            {
                if (_healthBuckets.Count > 1024)
                    foreach (var kv in _healthBuckets)
                        if (hnow - kv.Value.WindowStart > HealthBurstWindow) _healthBuckets.TryRemove(kv.Key, out _);
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"]             = "10";
                ctx.Response.Headers["X-Content-Type-Options"]  = "nosniff";
                ctx.Response.Headers["X-Frame-Options"]         = "DENY";
                ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                await ctx.Response.WriteAsJsonAsync(new { error = "rate limit exceeded on /health" });
                return;
            }
        }

        if (!IsHeavyPath(path))
        {
            await _next(ctx);
            return;
        }

        // API-key bucket. Key is hashed to keep buckets keyed by identity without
        // logging the bearer in dictionary state.
        if (ctx.Request.Headers.TryGetValue("X-API-Key", out var keyHeader) &&
            !string.IsNullOrEmpty(keyHeader.ToString()))
        {
            var keyHash = HashForBucket(keyHeader.ToString());
            // P15 hardening: when the dictionary is full (memory-growth attack
            // via random-key rotation), skip the per-key reservation for new
            // keys — existing key buckets still throttle, and the per-IP bucket
            // below still bounds anonymous floods.
            var atCap = _apiKeyBuckets.Count >= ApiKeyBucketHardCap;
            var isExisting = _apiKeyBuckets.ContainsKey(keyHash);
            if (!atCap || isExisting)
            {
                if (!TryReserve(_apiKeyBuckets, keyHash, _apiKeyCapacity))
                {
                    await Write429(ctx, $"rate limit exceeded; {_apiKeyCapacity} req/min per X-API-Key on heavy endpoints");
                    return;
                }
            }
        }

        var ip = ResolveClientIp(ctx);
        if (!TryReserve(_ipBuckets, ip, _ipCapacity))
        {
            await Write429(ctx, $"rate limit exceeded; {_ipCapacity} req/min per client IP on heavy endpoints");
            return;
        }

        MaybeEvict();
        await _next(ctx);
    }

    private bool TryReserve(
        ConcurrentDictionary<string, (DateTime WindowStart, int Count)> buckets,
        string key,
        int capacity)
    {
        var now = DateTime.UtcNow;
        var bucket = buckets.AddOrUpdate(key,
            _ => (now, 1),
            (_, b) => now - b.WindowStart > _window ? (now, 1) : (b.WindowStart, b.Count + 1));
        return bucket.Count <= capacity;
    }

    private void MaybeEvict()
    {
        if ((Interlocked.Increment(ref _tickCounter) % EvictEveryNTicks) != 0) return;
        var cutoff = DateTime.UtcNow - _window - _window;
        foreach (var kvp in _apiKeyBuckets)
            if (kvp.Value.WindowStart < cutoff) _apiKeyBuckets.TryRemove(kvp.Key, out _);
        foreach (var kvp in _ipBuckets)
            if (kvp.Value.WindowStart < cutoff) _ipBuckets.TryRemove(kvp.Key, out _);
    }

    private static async Task Write429(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }

    // Stable string hash of an X-API-Key for bucket keying. SHA-256 truncated
    // to 16 bytes hex — enough collision resistance for in-memory buckets,
    // doesn't expose the key in dictionary state for any heap dump diagnostic.
    private static string HashForBucket(string raw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 16);
    }

    // Post-UseForwardedHeaders RemoteIpAddress. The boilerplate does NOT wire
    // UseForwardedHeaders by default (single-container deploys speak directly
    // to Kestrel via the docker bridge) — clones that put themselves behind
    // Caddy MUST add UseForwardedHeaders + TRUSTED_PROXY_NETWORKS in Program.cs
    // BEFORE this middleware, otherwise rate-limit buckets are keyed by the
    // proxy IP. See ACP_OracleBot/Program.cs for the canonical wiring.
    private static string ResolveClientIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
