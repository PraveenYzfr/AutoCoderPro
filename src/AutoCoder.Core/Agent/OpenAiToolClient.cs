using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Agent;

/// <summary>OpenAI-style tools for DeepSeek (and other chat-completions hosts).</summary>
internal sealed class OpenAiToolClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _providerName;

    public OpenAiToolClient(HttpClient http, string apiKey, string model, string baseUrl, string? providerName = null)
    {
        _http = http;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _providerName = string.IsNullOrWhiteSpace(providerName) ? "openai" : providerName;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<GeminiTurn> GenerateAsync(
        string system,
        List<object> messages,
        CancellationToken cancellationToken)
    {
        LlmDailyBudget.Consume();
        var all = new List<object> { new { role = "system", content = system } };
        all.AddRange(messages);

        var model = _model;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = all,
            ["temperature"] = 0.2,
            ["max_tokens"] = 8192,
            ["tools"] = AgentToolSchema.OpenAiToolDefs()
        };
        if (_baseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            model = DeepSeekModels.Sanitize(model);
            payload["model"] = model;
            DeepSeekModels.ApplyThinking(payload, enable: false);
        }
        else if (_baseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase))
        {
            model = GroqModels.Sanitize(model);
            payload["model"] = model;
        }

        var url = $"{_baseUrl}/chat/completions";
        using var response = await TransientRetry.SendAsync(
            $"agent.{_providerName}",
            ct => _http.PostAsJsonAsync(url, payload, Json, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepSeek/OpenAI agent error {(int)response.StatusCode}: {raw[..Math.Min(800, raw.Length)]}");

        LlmUsage.AddOpenAiUsage(_providerName, model, raw);

        using var doc = JsonDocument.Parse(raw);
        var parts = new List<GeminiPart>();
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"No choices: {raw[..Math.Min(800, raw.Length)]}");
        }

        var message = choices[0].GetProperty("message");
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(new GeminiPart { Text = text });
        }

        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
                var fn = call.TryGetProperty("function", out var f) ? f : default;
                parts.Add(new GeminiPart
                {
                    ToolCallId = id,
                    FunctionName = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : null,
                    FunctionArgs = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("arguments", out var a)
                        ? a.GetString()
                        : "{}"
                });
            }
        }

        return new GeminiTurn { Parts = parts, Raw = raw };
    }

    // Tool schemas live in AgentToolSchema (includes Pro search_code + MCP).
}
