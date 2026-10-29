using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;

namespace SpirePilot.Core.Tests;

/// <summary>Minimal local HTTP server that records requests and replies with scripted responses.</summary>
internal sealed class FakeLlmServer : IDisposable
{
    public sealed record Received(string Path, Dictionary<string, string> Headers, JsonNode? Body);

    private readonly HttpListener _listener = new();
    private readonly Func<Received, int, (int status, string body)> _handler;
    private readonly Task _loop;
    public List<Received> Requests { get; } = new();
    public string BaseUrl { get; }

    public FakeLlmServer(Func<Received, int, (int status, string body)> handler)
    {
        _handler = handler;
        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            var headers = ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!.ToLowerInvariant(), k => ctx.Request.Headers[k]!);
            var rec = new Received(ctx.Request.Url!.AbsolutePath, headers, text.Length > 0 ? JsonNode.Parse(text) : null);
            int index;
            lock (Requests) { Requests.Add(rec); index = Requests.Count - 1; }
            var (status, body) = _handler(rec, index);
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

public class OpenAiCompatibleClientTests
{
    private static string Ok(string content) =>
        new JsonObject
        {
            ["model"] = "m",
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content }, ["finish_reason"] = "stop" }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 20 },
        }.ToJsonString();

    [Fact]
    public async Task Sends_chat_completion_with_auth_headers_and_json_mode()
    {
        using var server = new FakeLlmServer((_, _) => (200, Ok("{\"ok\":true}")));
        var profile = new LlmProfile
        {
            BaseUrl = server.BaseUrl + "/v1", ApiKey = "sk-test", Model = "deepseek-chat", JsonMode = true, Temperature = 0.3,
            ExtraHeaders = { ["X-Custom"] = "1" }, ExtraBodyJson = "{\"top_p\":0.9}",
        };
        var client = new OpenAiCompatibleClient(profile);
        var res = await client.CompleteAsync(new LlmRequest
        {
            System = "sys",
            Messages = { new LlmMessage(LlmRole.User, "hi") },
            JsonSchema = "{}",
        }, CancellationToken.None);

        Assert.Equal("{\"ok\":true}", res.Text);
        Assert.Equal(100, res.InputTokens);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/v1/chat/completions", req.Path);
        Assert.Equal("Bearer sk-test", req.Headers["authorization"]);
        Assert.Equal("1", req.Headers["x-custom"]);
        Assert.Equal("deepseek-chat", req.Body!["model"]!.ToString());
        Assert.Equal("system", req.Body["messages"]![0]!["role"]!.ToString());
        Assert.Equal("json_object", req.Body["response_format"]!["type"]!.ToString());
        Assert.Equal(0.3, (double)req.Body["temperature"]!);
        Assert.Equal(0.9, (double)req.Body["top_p"]!);
        Assert.NotNull(req.Body["max_tokens"]);
    }

    [Fact]
    public async Task Adapts_when_server_rejects_parameters()
    {
        using var server = new FakeLlmServer((r, i) => i switch
        {
            0 => (400, "{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead.\"}}"),
            1 => (400, "{\"error\":{\"message\":\"response_format json_object is not supported by this model\"}}"),
            _ => (200, Ok("{}")),
        });
        var client = new OpenAiCompatibleClient(new LlmProfile { BaseUrl = server.BaseUrl, ApiKey = "k", Model = "m", JsonMode = true });
        await client.CompleteAsync(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") }, JsonSchema = "{}" }, CancellationToken.None);

        Assert.Equal(3, server.Requests.Count);
        var last = server.Requests[2].Body!;
        Assert.Null(last["max_tokens"]);
        Assert.NotNull(last["max_completion_tokens"]);
        Assert.Null(last["response_format"]);
    }

    [Fact]
    public async Task Maps_rate_limits_to_retryable_errors()
    {
        using var server = new FakeLlmServer((_, _) => (429, "{\"error\":{\"message\":\"slow down\"}}"));
        var client = new OpenAiCompatibleClient(new LlmProfile { BaseUrl = server.BaseUrl, ApiKey = "k", Model = "m" });
        var ex = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") } }, CancellationToken.None));
        Assert.True(ex.Retryable);
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("slow down", ex.Message);
    }

    [Fact]
    public void Parses_array_content_parts()
    {
        var body = "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]},\"finish_reason\":\"stop\"}]}";
        Assert.Equal("ab", OpenAiCompatibleClient.Parse(body, TimeSpan.Zero).Text);
    }

    [Fact]
    public void Env_api_keys_are_resolved()
    {
        Environment.SetEnvironmentVariable("SPIREPILOT_TEST_KEY", "from-env");
        Assert.Equal("from-env", LlmHttp.ResolveApiKey("env:SPIREPILOT_TEST_KEY"));
        Assert.Equal("plain", LlmHttp.ResolveApiKey(" plain "));
    }
}

public class AnthropicClientTests
{
    private const string OkBody = """
    {"id":"msg_1","type":"message","model":"claude-opus-5-5","stop_reason":"end_turn",
     "content":[{"type":"thinking","thinking":""},{"type":"text","text":"{\"thought\":\"x\","},{"type":"text","text":"\"actions\":[\"a0\"]}"}],
     "usage":{"input_tokens":1200,"output_tokens":40,"cache_read_input_tokens":1000}}
    """;

    [Fact]
    public async Task Sends_messages_request_with_required_headers_caching_and_structured_output()
    {
        using var server = new FakeLlmServer((_, _) => (200, OkBody));
        var profile = new LlmProfile
        {
            Provider = LlmProvider.Anthropic, BaseUrl = server.BaseUrl, ApiKey = "sk-ant", Model = "claude-opus-5-5",
            ReasoningEffort = "low", JsonMode = true, Temperature = 0.5,
        };
        var res = await new AnthropicClient(profile).CompleteAsync(new LlmRequest
        {
            System = "rules",
            Messages = { new LlmMessage(LlmRole.User, "a"), new LlmMessage(LlmRole.User, "b") },
            JsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
        }, CancellationToken.None);

        Assert.Equal("{\"thought\":\"x\",\"actions\":[\"a0\"]}", res.Text);
        Assert.Equal(1000, res.CachedInputTokens);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/v1/messages", req.Path);
        Assert.Equal("sk-ant", req.Headers["x-api-key"]);
        Assert.Equal("2026-10-21", req.Headers["anthropic-version"]);
        Assert.False(req.Headers.ContainsKey("anthropic-beta")); // fallbacks only on api.anthropic.com
        var body = req.Body!;
        Assert.Null(body["temperature"]); // rejected by API version 2026-10-21
        Assert.Null(body["fallbacks"]);
        Assert.Equal("ephemeral", body["system"]![0]!["cache_control"]!["type"]!.ToString());
        Assert.Equal("low", body["output_config"]!["effort"]!.ToString());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.ToString());
        // consecutive user messages are merged so roles alternate
        Assert.Single(body["messages"]!.AsArray());
        Assert.Equal("a\n\nb", body["messages"]![0]!["content"]!.ToString());
    }

    [Fact]
    public async Task Uses_server_fallbacks_on_the_official_endpoint_only()
    {
        var official = new LlmProfile { Provider = LlmProvider.Anthropic, BaseUrl = "https://api.anthropic.com", ApiKey = "k", Model = "claude-opus-5-5" };
        using (var http = new AnthropicClient(official).BuildHttpRequest(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") } }))
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", http.RequestUri!.ToString());
            Assert.Equal("server-side-fallback-2026-07-01", string.Join(",", http.Headers.GetValues("anthropic-beta")));
            var body = JsonNode.Parse(await http.Content!.ReadAsStringAsync())!;
            Assert.Equal("default", body["fallbacks"]!.ToString());
        }

        var optedOut = official.Clone();
        optedOut.AnthropicServerFallback = false;
        using (var http = new AnthropicClient(optedOut).BuildHttpRequest(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") } }))
        {
            Assert.False(http.Headers.Contains("anthropic-beta"));
        }

        using var server = new FakeLlmServer((_, _) => (200, OkBody));
        var proxied = new LlmProfile { Provider = LlmProvider.Anthropic, BaseUrl = server.BaseUrl + "/v1", ApiKey = "k", Model = "claude-opus-5-5" };
        await new AnthropicClient(proxied).CompleteAsync(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") } }, CancellationToken.None);
        Assert.Equal("/v1/messages", server.Requests[0].Path); // no double /v1
        Assert.Null(server.Requests[0].Body!["fallbacks"]);
    }

    [Fact]
    public void Refusals_raise()
    {
        var body = "{\"type\":\"message\",\"stop_reason\":\"refusal\",\"stop_details\":{\"type\":\"refusal\",\"category\":\"cyber\"},\"content\":[]}";
        var ex = Assert.Throws<LlmException>(() => AnthropicClient.Parse(body, TimeSpan.Zero));
        Assert.Contains("cyber", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Drops_structured_output_when_a_gateway_rejects_it()
    {
        using var server = new FakeLlmServer((_, i) => i == 0
            ? (400, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"output_config.format: Extra inputs are not permitted\"}}")
            : (200, OkBody));
        var profile = new LlmProfile { Provider = LlmProvider.Anthropic, BaseUrl = server.BaseUrl, ApiKey = "k", Model = "m", JsonMode = true };
        await new AnthropicClient(profile).CompleteAsync(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") }, JsonSchema = "{}" }, CancellationToken.None);
        Assert.Equal(2, server.Requests.Count);
        Assert.Null(server.Requests[1].Body!["output_config"]);
    }
}
