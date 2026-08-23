using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Retrieval;

namespace AutoCoder.Tests;

public sealed class MemoryCodeIndexTests
{
    [Fact]
    public async Task Search_returns_the_chunk_matching_the_query_symbol()
    {
        var embedder = new DeterministicEmbedder();
        var index = new MemoryCodeIndex(embedder);
        var chunks = CodeChunker.ChunkFile("src/Auth.cs", """
            using System;

            public sealed class PasswordValidator
            {
                public bool IsValid(string password) => password.Contains('+');
            }
            """);
        await index.ReplaceAsync("test", "abc123", chunks);

        var hits = await index.SearchAsync("test", "password validator plus encoding", topK: 3);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Chunk.RelativePath.Contains("Auth.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CodeIndexService_indexes_a_workspace_and_answers_search_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "acpro-idx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Billing.cs"), """
            namespace App;
            public sealed class InvoiceService
            {
                public decimal Total(decimal net) => net * 1.1m;
            }
            """);

        try
        {
            var options = new RetrievalOptions
            {
                Enabled = true,
                Backend = "memory",
                Embedder = "deterministic",
                TopK = 5,
                MaxFilesToIndex = 100
            };
            var service = new CodeIndexService(options, new MemoryCodeIndex(new DeterministicEmbedder()), new DeterministicEmbedder());
            var (count, _) = await service.IndexWorkspaceAsync(root);
            Assert.True(count >= 1);

            var result = await service.SearchAsync(root, "invoice total tax");
            Assert.Contains("Billing.cs", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryCreate_returns_null_when_retrieval_disabled()
    {
        var options = new AutoCoderOptions();
        options.Retrieval.Enabled = false;
        Assert.Null(CodeIndexService.TryCreate(options));
    }

    [Fact]
    public void TryCreate_returns_memory_backend_when_enabled()
    {
        var options = new AutoCoderOptions();
        options.Retrieval.Enabled = true;
        options.Retrieval.Backend = "memory";
        var service = CodeIndexService.TryCreate(options);
        Assert.NotNull(service);
        Assert.Equal("memory", service!.Backend);
    }
}
