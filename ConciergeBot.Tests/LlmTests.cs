using System.Net;
using System.Text;
using ConciergeBot.Api.Services.Llm;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ConciergeBot.Tests;

public class LlmOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> vals) =>
        new ConfigurationBuilder().AddInMemoryCollection(vals).Build();

    private static readonly Dictionary<string, string?> NoEnv = new();

    [Fact]
    public void Defaults_apply_when_unset()
    {
        var o = LlmOptions.FromConfiguration(Config(new()), NoEnv);
        Assert.Equal(LlmProvider.Disabled, o.Provider);
        Assert.Equal("https://compute.virtuals.io/v1", o.Endpoint);
        Assert.Equal("moonshotai/kimi-k2-0905", o.Model);
        Assert.Null(o.ApiKey);
    }

    [Theory]
    [InlineData("disabled", LlmProvider.Disabled)]
    [InlineData("openai-compatible", LlmProvider.OpenAiCompatible)]
    [InlineData("virtuals-compute", LlmProvider.VirtualsCompute)]
    [InlineData("VIRTUALS-COMPUTE", LlmProvider.VirtualsCompute)]
    [InlineData(" openai-compatible ", LlmProvider.OpenAiCompatible)]
    public void Parses_known_providers(string raw, LlmProvider expected)
    {
        var o = LlmOptions.FromConfiguration(Config(new() { ["Llm:Provider"] = raw }), NoEnv);
        Assert.Equal(expected, o.Provider);
    }

    [Fact]
    public void Unknown_provider_fails_closed_to_disabled()
    {
        var o = LlmOptions.FromConfiguration(Config(new() { ["Llm:Provider"] = "gpt-9-turbo" }), NoEnv);
        Assert.Equal(LlmProvider.Disabled, o.Provider);
    }

    [Fact]
    public void Endpoint_and_model_overridable()
    {
        var o = LlmOptions.FromConfiguration(Config(new()
        {
            ["Llm:Endpoint"] = "https://example.test/v1",
            ["Llm:Model"] = "some/other-model"
        }), NoEnv);
        Assert.Equal("https://example.test/v1", o.Endpoint);
        Assert.Equal("some/other-model", o.Model);
    }

    [Fact]
    public void ApiKey_resolves_from_VIRTUALS_API_KEY_env()
    {
        var o = LlmOptions.FromConfiguration(
            Config(new() { ["Llm:Provider"] = "virtuals-compute" }),
            new Dictionary<string, string?> { ["VIRTUALS_API_KEY"] = "sk-secret" });
        Assert.Equal("sk-secret", o.ApiKey);
    }

    [Fact]
    public void Config_ApiKey_overrides_env()
    {
        var o = LlmOptions.FromConfiguration(
            Config(new() { ["Llm:ApiKey"] = "cfg-key" }),
            new Dictionary<string, string?> { ["VIRTUALS_API_KEY"] = "env-key" });
        Assert.Equal("cfg-key", o.ApiKey);
    }
}

public class DisabledLlmClientTests
{
    [Fact]
    public void Reports_disabled()
    {
        var c = new DisabledLlmClient(new LlmOptions { Provider = LlmProvider.Disabled, Endpoint = "x", Model = "m" });
        Assert.False(c.IsEnabled);
        Assert.Equal("disabled", c.ProviderLabel);
        Assert.Equal("m", c.Model);
    }

    [Fact]
    public async Task Completion_is_a_safe_disabled_result_with_no_network()
    {
        var c = new DisabledLlmClient(new LlmOptions { Provider = LlmProvider.Disabled, Endpoint = "x", Model = "m" });
        var r = await c.CompleteAsync("sys", "user", default);
        Assert.False(r.Ok);
        Assert.Equal("llm_disabled", r.Error);
        Assert.Null(r.Text);
    }
}

public class OpenAiCompatibleLlmClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public int Calls;
        public HttpRequestMessage? Last;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            return Task.FromResult(_fn(request));
        }
    }

    private static LlmOptions Opts(string? key) => new()
    {
        Provider = LlmProvider.VirtualsCompute,
        Endpoint = "https://compute.virtuals.io/v1",
        Model = "moonshotai/kimi-k2-0905",
        ApiKey = key
    };

    [Fact]
    public async Task Parses_assistant_content_on_success()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","content":"Hello from compute."}}]}""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var c = new OpenAiCompatibleLlmClient(new HttpClient(handler), Opts("sk-x"));

        Assert.True(c.IsEnabled);
        Assert.Equal("virtuals-compute", c.ProviderLabel);

        var r = await c.CompleteAsync("system prompt", "user prompt", default);

        Assert.True(r.Ok);
        Assert.Equal("Hello from compute.", r.Text);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Bearer sk-x", handler.Last!.Headers.Authorization?.ToString());
        Assert.EndsWith("/chat/completions", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Missing_key_short_circuits_without_network()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var c = new OpenAiCompatibleLlmClient(new HttpClient(handler), Opts(null));

        Assert.False(c.IsEnabled);
        var r = await c.CompleteAsync("s", "u", default);

        Assert.False(r.Ok);
        Assert.Equal("missing_api_key", r.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Upstream_error_body_is_never_relayed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal detail with leaked-secret sk-zzz")
        });
        var c = new OpenAiCompatibleLlmClient(new HttpClient(handler), Opts("sk-x"));

        var r = await c.CompleteAsync("s", "u", default);

        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.DoesNotContain("sk-zzz", r.Error!);
        Assert.DoesNotContain("leaked-secret", r.Error!);
    }

    [Fact]
    public async Task Empty_choices_reports_empty_response()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[]}""", Encoding.UTF8, "application/json")
        });
        var c = new OpenAiCompatibleLlmClient(new HttpClient(handler), Opts("sk-x"));

        var r = await c.CompleteAsync("s", "u", default);

        Assert.False(r.Ok);
        Assert.Equal("empty_response", r.Error);
    }
}
