using System.Diagnostics;
using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Agent;

namespace AutoCoder.Core.Retrieval;

/// <summary>
/// Builds and queries the per-run code index. Re-indexes when the commit SHA changes so the
/// index never confidently describes a repo that has moved on (item 11).
/// </summary>
public sealed class CodeIndexService
{
    private readonly RetrievalOptions _options;
    private readonly ICodeIndex _index;
    private readonly IEmbedder _embedder;

    public CodeIndexService(RetrievalOptions options, ICodeIndex index, IEmbedder embedder)
    {
        _options = options;
        _index = index;
        _embedder = embedder;
    }

    public string Backend => _index.Backend;
    public string Embedder => _embedder.Name;

    public static CodeIndexService? TryCreate(AutoCoderOptions options)
    {
        var r = options.Retrieval;
        if (!r.Enabled)
            return null;

        IEmbedder embedder = CreateEmbedder(r);
        ICodeIndex index = r.Backend.Trim().ToLowerInvariant() switch
        {
            "qdrant" => new QdrantCodeIndex(
                string.IsNullOrWhiteSpace(r.QdrantUrl) ? "http://qdrant:6333" : r.QdrantUrl,
                embedder),
            _ => new MemoryCodeIndex(embedder)
        };
        return new CodeIndexService(r, index, embedder);
    }

    public async Task<(int Chunks, string CommitSha)> IndexWorkspaceAsync(
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        var commit = await ReadCommitShaAsync(workDirectory, cancellationToken) ?? "unknown";
        var files = EnumerateIndexable(workDirectory).Take(Math.Max(1, _options.MaxFilesToIndex)).ToList();
        var chunks = new List<CodeChunk>();
        foreach (var full in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = WorkspacePaths.Relativize(workDirectory, full);
            string text;
            try
            {
                text = await File.ReadAllTextAsync(full, cancellationToken);
            }
            catch
            {
                continue;
            }
            if (text.Length > 400_000)
                text = text[..400_000];
            chunks.AddRange(CodeChunker.ChunkFile(rel, text, _options.MaxChunkChars));
        }

        var collection = CollectionName(workDirectory);
        await _index.ReplaceAsync(collection, commit, chunks, cancellationToken);
        return (chunks.Count, commit);
    }

    public async Task<string> SearchAsync(string workDirectory, string query, int? topK = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "search_code requires a non-empty query.";

        var hits = await _index.SearchAsync(
            CollectionName(workDirectory),
            query,
            topK ?? _options.TopK,
            cancellationToken);

        if (hits.Count == 0)
            return "No retrieval hits. Try a more specific symbol or path keyword, or use grep.";

        var blocks = hits.Select(h =>
        {
            var c = h.Chunk;
            var preview = c.Body.Length <= 1_200 ? c.Body : c.Body[..1_200] + "\n…";
            return $"""
                ### {c.RelativePath}:{c.StartLine}-{c.EndLine}  ({c.Kind} {c.Symbol})  score={h.Score:F3}
                {(string.IsNullOrWhiteSpace(c.Imports) ? "" : c.Imports + "\n")}
                {preview}
                """;
        });
        return string.Join("\n\n", blocks);
    }

    public async Task<string> ScoutContextAsync(string workDirectory, string ticketText, CancellationToken cancellationToken = default)
    {
        var hits = await _index.SearchAsync(
            CollectionName(workDirectory),
            ticketText,
            Math.Max(_options.TopK, 12),
            cancellationToken);
        if (hits.Count == 0)
            return "(retrieval: no hits)";

        return string.Join("\n\n", hits.Select(h =>
        {
            var c = h.Chunk;
            var preview = c.Body.Length <= 800 ? c.Body : c.Body[..800] + "\n…";
            return $"- {c.RelativePath}:{c.StartLine}-{c.EndLine} ({c.Kind} {c.Symbol})\n{preview}";
        }));
    }

    public int CountIndexableFiles(string workDirectory) =>
        EnumerateIndexable(workDirectory).Take(_options.MaxFilesToIndex + 1).Count();

    private IEnumerable<string> EnumerateIndexable(string workDirectory)
    {
        if (!Directory.Exists(workDirectory))
            yield break;

        foreach (var full in Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories))
        {
            var rel = WorkspacePaths.Relativize(workDirectory, full);
            if (WorkspacePaths.IsIgnored(rel))
                continue;
            if (!CodeChunker.IsIndexable(rel))
                continue;
            yield return full;
        }
    }

    private string CollectionName(string workDirectory)
    {
        var baseName = string.IsNullOrWhiteSpace(_options.Collection) ? "autocoderpro-code" : _options.Collection.Trim();
        // Per-workspace suffix keeps concurrent runs from clobbering each other in shared Qdrant.
        var hash = Math.Abs(workDirectory.ToLowerInvariant().GetHashCode());
        return $"{baseName}-{hash}";
    }

    private static IEmbedder CreateEmbedder(RetrievalOptions r)
    {
        var kind = (r.Embedder ?? "deterministic").Trim().ToLowerInvariant();
        if (kind is "openai" or "openai-compatible")
        {
            var key = Environment.GetEnvironmentVariable("EMBEDDING_API_KEY")
                      ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(key))
            {
                return new OpenAiEmbedder(
                    key,
                    string.IsNullOrWhiteSpace(r.EmbeddingModel) ? "text-embedding-3-small" : r.EmbeddingModel,
                    r.EmbeddingEndpoint);
            }
            Console.WriteLine("[retrieval] No EMBEDDING_API_KEY/OPENAI_API_KEY; using deterministic embedder.");
        }
        return new DeterministicEmbedder();
    }

    private static async Task<string?> ReadCommitShaAsync(string workDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Ambient handle so the coding agent can call search_code without threading the service everywhere.</summary>
public static class CodeIndexAmbient
{
    private static readonly AsyncLocal<CodeIndexService?> Current = new();
    private static readonly AsyncLocal<string?> WorkDir = new();

    public static void Set(CodeIndexService? service, string? workDirectory)
    {
        Current.Value = service;
        WorkDir.Value = workDirectory;
    }

    public static CodeIndexService? Service => Current.Value;
    public static string? WorkDirectory => WorkDir.Value;
}
