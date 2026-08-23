namespace AutoCoder.Core.Retrieval;

public sealed class CodeHit
{
    public required CodeChunk Chunk { get; init; }
    public float Score { get; init; }
}

public interface ICodeIndex
{
    string Backend { get; }
    Task ReplaceAsync(string collection, string commitSha, IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CodeHit>> SearchAsync(string collection, string query, int topK, CancellationToken cancellationToken = default);
}

/// <summary>In-process lexical + optional vector search. Always available; no Qdrant required.</summary>
public sealed class MemoryCodeIndex(IEmbedder embedder) : ICodeIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Commit, List<(CodeChunk Chunk, float[]? Vector, HashSet<string> Tokens)> Entries)> _store = new(StringComparer.OrdinalIgnoreCase);

    public string Backend => "memory";

    public async Task ReplaceAsync(string collection, string commitSha, IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        var entries = new List<(CodeChunk, float[]?, HashSet<string>)>(chunks.Count);
        const int batch = 32;
        for (var i = 0; i < chunks.Count; i += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = chunks.Skip(i).Take(batch).ToList();
            var vectors = await embedder.EmbedBatchAsync(slice.Select(c => c.TextForEmbed).ToList(), cancellationToken);
            for (var j = 0; j < slice.Count; j++)
            {
                var tokens = new HashSet<string>(DeterministicEmbedder.Tokenize(slice[j].TextForEmbed), StringComparer.Ordinal);
                entries.Add((slice[j], vectors[j], tokens));
            }
        }

        lock (_gate)
            _store[collection] = (commitSha, entries);
    }

    public async Task<IReadOnlyList<CodeHit>> SearchAsync(string collection, string query, int topK, CancellationToken cancellationToken = default)
    {
        List<(CodeChunk Chunk, float[]? Vector, HashSet<string> Tokens)> entries;
        lock (_gate)
        {
            if (!_store.TryGetValue(collection, out var bag) || bag.Entries.Count == 0)
                return [];
            entries = bag.Entries;
        }

        var qVec = await embedder.EmbedAsync(query, cancellationToken);
        var qTokens = new HashSet<string>(DeterministicEmbedder.Tokenize(query), StringComparer.Ordinal);
        var scored = new List<CodeHit>(entries.Count);
        foreach (var (chunk, vector, tokens) in entries)
        {
            var lexical = LexicalScore(qTokens, tokens);
            var cosine = vector is null ? 0f : Cosine(qVec, vector);
            var score = lexical * 0.55f + cosine * 0.45f;
            if (score <= 0)
                continue;
            scored.Add(new CodeHit { Chunk = chunk, Score = score });
        }

        return scored.OrderByDescending(h => h.Score).Take(Math.Max(1, topK)).ToList();
    }

    private static float LexicalScore(HashSet<string> query, HashSet<string> doc)
    {
        if (query.Count == 0 || doc.Count == 0)
            return 0;
        var hit = query.Count(doc.Contains);
        return (float)hit / query.Count;
    }

    private static float Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na <= 0 || nb <= 0)
            return 0;
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
