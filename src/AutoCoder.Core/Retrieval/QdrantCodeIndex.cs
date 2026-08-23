using System.Net.Http.Json;
using System.Text.Json;

namespace AutoCoder.Core.Retrieval;

/// <summary>Qdrant REST client — same pattern as SeekandDestroy on the estate VM.</summary>
public sealed class QdrantCodeIndex : ICodeIndex, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IEmbedder _embedder;

    public string Backend => "qdrant";

    public QdrantCodeIndex(string baseUrl, IEmbedder embedder, HttpClient? http = null)
    {
        _embedder = embedder;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }

    public async Task ReplaceAsync(string collection, string commitSha, IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(collection, cancellationToken);
        // Wipe + re-upsert keyed by commit so an outdated index cannot describe a moved-on repo.
        await DeleteCollectionAsync(collection, cancellationToken);
        await EnsureCollectionAsync(collection, cancellationToken);

        const int batch = 32;
        for (var i = 0; i < chunks.Count; i += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = chunks.Skip(i).Take(batch).ToList();
            var vectors = await _embedder.EmbedBatchAsync(slice.Select(c => c.TextForEmbed).ToList(), cancellationToken);
            var points = new List<object>(slice.Count);
            for (var j = 0; j < slice.Count; j++)
            {
                var c = slice[j];
                points.Add(new
                {
                    id = StableId(c.Id),
                    vector = vectors[j],
                    payload = new Dictionary<string, object?>
                    {
                        ["path"] = c.RelativePath,
                        ["symbol"] = c.Symbol,
                        ["kind"] = c.Kind,
                        ["language"] = c.Language,
                        ["imports"] = c.Imports,
                        ["body"] = Truncate(c.Body, 6_000),
                        ["start_line"] = c.StartLine,
                        ["end_line"] = c.EndLine,
                        ["commit"] = commitSha,
                        ["chunk_id"] = c.Id
                    }
                });
            }

            using var resp = await _http.PutAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collection)}/points?wait=true",
                new { points },
                cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Qdrant upsert {(int)resp.StatusCode}: {Truncate(err, 400)}");
            }
        }
    }

    public async Task<IReadOnlyList<CodeHit>> SearchAsync(string collection, string query, int topK, CancellationToken cancellationToken = default)
    {
        var vector = await _embedder.EmbedAsync(query, cancellationToken);
        using var resp = await _http.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collection)}/points/search",
            new { vector, limit = Math.Max(1, topK), with_payload = true },
            cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qdrant search {(int)resp.StatusCode}: {Truncate(raw, 400)}");

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("result", out var result))
            return [];

        var hits = new List<CodeHit>();
        foreach (var item in result.EnumerateArray())
        {
            var score = item.TryGetProperty("score", out var s) ? s.GetSingle() : 0f;
            if (!item.TryGetProperty("payload", out var payload))
                continue;
            var chunk = new CodeChunk
            {
                Id = Str(payload, "chunk_id") ?? Guid.NewGuid().ToString("N"),
                RelativePath = Str(payload, "path") ?? "",
                Symbol = Str(payload, "symbol") ?? "",
                Kind = Str(payload, "kind") ?? "file",
                Language = Str(payload, "language") ?? "text",
                Imports = Str(payload, "imports") ?? "",
                Body = Str(payload, "body") ?? "",
                StartLine = Int(payload, "start_line"),
                EndLine = Int(payload, "end_line")
            };
            hits.Add(new CodeHit { Chunk = chunk, Score = score });
        }
        return hits;
    }

    private async Task EnsureCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        using var get = await _http.GetAsync($"collections/{Uri.EscapeDataString(collection)}", cancellationToken);
        if (get.IsSuccessStatusCode)
        {
            var raw = await get.Content.ReadAsStringAsync(cancellationToken);
            if (TryReadVectorSize(raw, out var existing) && existing != _embedder.Dimensions)
            {
                Console.WriteLine(
                    $"[qdrant] Collection '{collection}' has size={existing}, embedder wants {_embedder.Dimensions} — recreating.");
                await DeleteCollectionAsync(collection, cancellationToken);
            }
            else
            {
                return;
            }
        }

        using var create = await _http.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collection)}",
            new
            {
                vectors = new { size = _embedder.Dimensions, distance = "Cosine" }
            },
            cancellationToken);
        if (!create.IsSuccessStatusCode)
        {
            var err = await create.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Qdrant create collection {(int)create.StatusCode}: {Truncate(err, 400)}");
        }
    }

    private async Task DeleteCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        using var del = await _http.DeleteAsync($"collections/{Uri.EscapeDataString(collection)}", cancellationToken);
        // 404 is fine — collection did not exist yet.
        // After delete we always EnsureCollection again (caller) so clear-without-recreate cannot stick.
    }

    private static bool TryReadVectorSize(string collectionJson, out int size)
    {
        size = 0;
        try
        {
            using var doc = JsonDocument.Parse(collectionJson);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return false;
            if (!result.TryGetProperty("config", out var config))
                return false;
            if (!config.TryGetProperty("params", out var parameters))
                return false;
            if (!parameters.TryGetProperty("vectors", out var vectors))
                return false;
            // Single unnamed vector: { "size": 768, "distance": "Cosine" }
            if (vectors.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt32(out size))
                return true;
            // Named vectors map — take the first.
            foreach (var prop in vectors.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("size", out var n) && n.TryGetInt32(out size))
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static ulong StableId(string chunkId)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var c in chunkId)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
