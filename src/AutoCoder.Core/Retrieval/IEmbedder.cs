using System.Net.Http.Json;

namespace AutoCoder.Core.Retrieval;

public interface IEmbedder
{
    string Name { get; }
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic local vectors — explicit lightweight opt-in only (`embedder: deterministic`).
/// Never selected automatically when gemini/openai is configured; that silent swap is how mocks ship.
/// </summary>
public sealed class DeterministicEmbedder : IEmbedder
{
    public string Name => "deterministic";
    public int Dimensions { get; }

    public DeterministicEmbedder(int dimensions = 384) => Dimensions = Math.Clamp(dimensions, 64, 1024);

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    private float[] Embed(string text)
    {
        var vec = new float[Dimensions];
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return vec;

        foreach (var token in tokens)
        {
            var hash = StableHash(token);
            var idx = (int)(hash % (uint)Dimensions);
            var sign = (hash & 1) == 0 ? 1f : -1f;
            vec[idx] += sign;
        }

        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vec.Length; i++)
                vec[i] /= norm;
        }
        return vec;
    }

    internal static IReadOnlyList<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '<', '>', '=', '+', '-', '*', '!', '?'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Take(2_000)
            .ToList();

    private static uint StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }
    }
}

/// <summary>OpenAI embeddings — benchmark / opt-in only. Default Pro path is Gemini.</summary>
public sealed class OpenAiEmbedder : IEmbedder, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _model;
    private readonly string _url;

    public string Name => "openai";
    public int Dimensions { get; }

    public OpenAiEmbedder(
        string apiKey,
        string model = "text-embedding-3-small",
        string? endpoint = null,
        int dimensions = 1536,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Embedding API key required.", nameof(apiKey));
        _model = model;
        Dimensions = dimensions;
        var baseUrl = (endpoint ?? "https://api.openai.com/v1").TrimEnd('/');
        _url = $"{baseUrl}/embeddings";
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var batch = await EmbedBatchAsync([text], cancellationToken);
        return batch[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var payload = new { model = _model, input = texts };
        using var response = await _http.PostAsJsonAsync(_url, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Embedding API {(int)response.StatusCode}: {raw[..Math.Min(raw.Length, 400)]}");

        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        var list = new float[data.GetArrayLength()][];
        foreach (var item in data.EnumerateArray())
        {
            var idx = item.GetProperty("index").GetInt32();
            var emb = item.GetProperty("embedding");
            var vec = new float[emb.GetArrayLength()];
            var i = 0;
            foreach (var n in emb.EnumerateArray())
                vec[i++] = n.GetSingle();
            list[idx] = vec;
        }
        return list!;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
