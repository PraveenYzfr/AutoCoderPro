using System.Net.Http.Json;
using System.Text.Json;

namespace AutoCoder.Core.Retrieval;

/// <summary>
/// Gemini Embedding API (GEMINI_API_KEY / GOOGLE_API_KEY). Default for AutoCoderPro —
/// estate cost order keeps OpenAI for benchmarking; B/SeekandDestroy already uses Gemini embeddings.
/// Uses outputDimensionality (default 768) so Qdrant upserts stay small — full 3072 is a known footgun.
/// </summary>
public sealed class GeminiEmbedder : IEmbedder, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _model;

    public string Name => "gemini";
    public int Dimensions { get; }

    public GeminiEmbedder(
        string apiKey,
        string model = "gemini-embedding-001",
        int dimensions = 768,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Gemini API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-embedding-001" : model.Trim();
        Dimensions = Math.Clamp(dimensions, 128, 3072);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var batch = await EmbedBatchAsync([text], cancellationToken);
        return batch[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        // batchEmbedContents keeps indexing under the same dimension contract as single calls.
        var modelPath = _model.StartsWith("models/", StringComparison.Ordinal) ? _model : $"models/{_model}";
        var requests = texts.Select(t => new Dictionary<string, object?>
        {
            ["model"] = modelPath,
            ["content"] = new { parts = new[] { new { text = t } } },
            ["taskType"] = "RETRIEVAL_DOCUMENT",
            ["outputDimensionality"] = Dimensions
        }).ToList();

        var url =
            $"https://generativelanguage.googleapis.com/v1beta/{modelPath}:batchEmbedContents?key={Uri.EscapeDataString(_apiKey)}";
        using var response = await _http.PostAsJsonAsync(url, new { requests }, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini embed {(int)response.StatusCode}: {Truncate(raw, 400)}");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("embeddings", out var embeddings))
            throw new InvalidOperationException("Gemini embed response missing embeddings[].");

        var list = new List<float[]>(embeddings.GetArrayLength());
        foreach (var item in embeddings.EnumerateArray())
        {
            if (!item.TryGetProperty("values", out var values))
                throw new InvalidOperationException("Gemini embed item missing values[].");
            var vec = new float[values.GetArrayLength()];
            var i = 0;
            foreach (var n in values.EnumerateArray())
                vec[i++] = n.GetSingle();
            // gemini-embedding-001 truncated dims are not always L2-normalized — normalize for cosine.
            NormalizeInPlace(vec);
            list.Add(vec);
        }

        if (list.Count != texts.Count)
            throw new InvalidOperationException($"Gemini returned {list.Count} embeddings for {texts.Count} inputs.");

        return list;
    }

    private static void NormalizeInPlace(float[] vec)
    {
        double sum = 0;
        foreach (var v in vec)
            sum += v * v;
        var norm = Math.Sqrt(sum);
        if (norm <= 0)
            return;
        for (var i = 0; i < vec.Length; i++)
            vec[i] = (float)(vec[i] / norm);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
