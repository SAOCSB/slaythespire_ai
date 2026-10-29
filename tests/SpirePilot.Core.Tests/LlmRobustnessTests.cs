using System.Text.Json.Nodes;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>Edge cases of real providers and gateways: token limits, odd headers, odd response shapes.</summary>
public class LlmRobustnessTests
{
    private static string Ok(string content, JsonNode? usage = null) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content }, ["finish_reason"] = "stop" }),
            ["usage"] = usage ?? new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
        }.ToJsonString();

    private static LlmRequest Req(int? maxTokens = null) => new() { Messages = { new LlmMessage(LlmRole.User, "x") }, MaxTokens = maxTokens };

    [Fact]
    public async Task Too_large_max_tokens_uses_the_stated_limit_for_that_request_only()
    {
        using var server = new FakeLlmServer((r, i) =>
            (int)r.Body!["max_tokens"]! > 65536
                ? (400, "{\"error\":{\"message\":\"Invalid max_tokens value, the valid range of max_tokens is [1, 65536]\"}}")
                : (200, Ok("{}")));
        var client = new OpenAiCompatibleClient(new LlmProfile { BaseUrl = server.BaseUrl, ApiKey = "k", Model = "deepseek-reasoner", MaxTokens = 100000 });
        await client.CompleteAsync(Req(), CancellationToken.None);
        await client.CompleteAsync(Req(1000), CancellationToken.None);

        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(65536, (int)server.Requests[1].Body!["max_tokens"]!);
        Assert.Equal(1000, (int)server.Requests[2].Body!["max_tokens"]!); // not capped, field not renamed
        Assert.All(server.Requests, r => Assert.Null(r.Body!["max_completion_tokens"]));
    }

    [Fact]
    public void Context_window_errors_leave_room_for_the_prompt()
    {
        var lower = OpenAiCompatibleClient.TryLowerTokens(
            "'max_tokens' is too large: 4096. This model's maximum context length is 8192 tokens and your request has 5000 input tokens (4096 > 8192 - 5000).", 4096);
        Assert.Equal(8192 - 5000 - 64, lower);
        Assert.Null(OpenAiCompatibleClient.TryLowerTokens("Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead.", 4096));
        Assert.Null(OpenAiCompatibleClient.TryLowerTokens("temperature must be between 0 and 2", 4096));
    }

    [Fact]
    public async Task Combat_effort_is_sent_even_without_a_general_effort()
    {
        using var server = new FakeLlmServer((_, _) => (200, Ok("{}")));
        var client = new OpenAiCompatibleClient(new LlmProfile { BaseUrl = server.BaseUrl, ApiKey = "k", Model = "gpt-5" });
        await client.CompleteAsync(new LlmRequest { Messages = { new LlmMessage(LlmRole.User, "x") }, Effort = "low" }, CancellationToken.None);
        await client.CompleteAsync(Req(), CancellationToken.None);
        Assert.Equal("low", server.Requests[0].Body!["reasoning_effort"]!.ToString());
        Assert.Null(server.Requests[1].Body!["reasoning_effort"]);
    }

    [Fact]
    public async Task Extra_headers_tolerate_content_headers_and_merge_anthropic_betas()
    {
        using var server = new FakeLlmServer((_, _) => (200,
            "{\"type\":\"message\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"cache_read_input_tokens\":300,\"cache_creation_input_tokens\":5}}"));
        var profile = LlmPresets.FromPreset(LlmPresets.Find("anthropic")!);
        profile.ApiKey = "k";
        profile.BaseUrl = server.BaseUrl + "/api.anthropic.com"; // still counts as the official host for the fallback beta
        profile.ExtraHeaders = new() { ["Content-Type"] = "application/json", ["anthropic-beta"] = "context-management-2025-06-27" };
        var response = await new AnthropicClient(profile).CompleteAsync(Req(), CancellationToken.None);

        var beta = server.Requests[0].Headers["anthropic-beta"];
        Assert.Contains("server-side-fallback-2026-07-01", beta);
        Assert.Contains("context-management-2025-06-27", beta);
        Assert.Equal(315, response.InputTokens); // all input, like OpenAI's prompt_tokens
        Assert.Equal(300, response.CachedInputTokens);
    }

    [Fact]
    public async Task Malformed_settings_are_config_errors()
    {
        var bad = new LlmProfile { BaseUrl = "http://127.0.0.1:9/v1", ApiKey = "k", Model = "m", ExtraHeaders = new() { ["X-Key:"] = "v" } };
        var ex = await Assert.ThrowsAsync<LlmException>(() => new OpenAiCompatibleClient(bad).CompleteAsync(Req(), CancellationToken.None));
        Assert.True(ex.IsConfigError);
        Assert.Contains("extra_headers", LlmClientFactory.Validate(bad));

        var badBody = new LlmProfile { BaseUrl = "http://127.0.0.1:9/v1", ApiKey = "k", Model = "m", ExtraBodyJson = "{enable_thinking:false}" };
        ex = await Assert.ThrowsAsync<LlmException>(() => new OpenAiCompatibleClient(badBody).CompleteAsync(Req(), CancellationToken.None));
        Assert.True(ex.IsConfigError);
        Assert.Contains("extra_body", LlmClientFactory.Validate(badBody));
    }

    [Fact]
    public void Odd_response_shapes_raise_llm_exceptions_or_parse()
    {
        Assert.Throws<LlmException>(() => OpenAiCompatibleClient.Parse("{\"choices\":[]}", TimeSpan.Zero));
        Assert.Throws<LlmException>(() => OpenAiCompatibleClient.Parse("[1,2]", TimeSpan.Zero));
        Assert.Throws<LlmException>(() => AnthropicClient.Parse("[]", TimeSpan.Zero));
        var r = OpenAiCompatibleClient.Parse(Ok("{}", new JsonObject { ["prompt_tokens"] = 1234.0, ["completion_tokens"] = "12" }), TimeSpan.Zero);
        Assert.Equal(1234, r.InputTokens);
        Assert.Equal(12, r.OutputTokens);
    }

    [Theory]
    [InlineData("http://192.168.1.20:11434/v1", true)]
    [InlineData("http://10.0.0.5:8000/v1", true)]
    [InlineData("http://172.20.1.1:8000/v1", true)]
    [InlineData("http://gpu-box:8000/v1", true)]
    [InlineData("http://[fd00::1]:8000/v1", true)]
    [InlineData("https://api.deepseek.com/v1", false)]
    [InlineData("http://172.32.0.1/v1", false)]
    public void Lan_servers_need_no_key(string url, bool local)
    {
        var problems = LlmClientFactory.Validate(new LlmProfile { BaseUrl = url, Model = "m" });
        Assert.Equal(!local, problems.Contains("api_key"));
    }

    [Fact]
    public void Reasoning_without_an_opening_tag_is_stripped()
    {
        var text = "Draft: {\"thought\":\"strike\",\"actions\":[\"a1\"]}. Wait, a3 is better</think>{\"thought\":\"bash\",\"actions\":[\"a3\"]}";
        Assert.Equal("{\"thought\":\"bash\",\"actions\":[\"a3\"]}", DecisionParser.StripReasoning(text));
        Assert.Equal("{}", DecisionParser.StripReasoning("<think>a {x}</think>{}"));
    }

    [Fact]
    public void Reasoning_presets_get_room_to_think()
    {
        Assert.True(LlmPresets.FromPreset(LlmPresets.Find("openai")!).MaxTokens >= 25000);
        Assert.True(LlmPresets.FromPreset(LlmPresets.Find("gemini")!).MaxTokens >= 16000);
        Assert.True(LlmPresets.FromPreset(LlmPresets.Find("zhipu")!).MaxTokens >= 16000);
    }

    private sealed class FailingClient : ILlmClient
    {
        public int Calls;
        public string Describe => "failing";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new LlmException("HTTP 503: overloaded", 503);
        }
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    [Fact]
    public async Task Without_the_fallback_player_repeated_failures_pause_instead_of_retrying_forever()
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.FallbackOnLlmFailure = false;
        settings.Normalize();
        var client = new FailingClient();
        var engine = new DecisionEngine(() => settings, new TeamChatHub(), new NullLog(), _ => client);
        for (int i = 0; i < 2; i++)
            await Assert.ThrowsAsync<LlmException>(() => engine.DecideAsync(TestData.Combat(), null, CancellationToken.None));
        await Assert.ThrowsAsync<PilotConfigException>(() => engine.DecideAsync(TestData.Combat(), null, CancellationToken.None));
    }
}
