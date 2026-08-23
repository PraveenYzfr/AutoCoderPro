using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Retrieval;

namespace AutoCoder.Core.Pipelines;

/// <summary>
/// AutoCoderPro: index the cloned workspace after ProvisionSandbox so scout + coding agent can
/// retrieve by meaning instead of walking files one at a time (item 11).
/// </summary>
public sealed class IndexRepoStep(AutoCoderOptions options) : IPipelineStep
{
    public string Name => "IndexRepo";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Retrieval.Enabled)
        {
            Console.WriteLine($"[{Name}] Retrieval disabled — skip.");
            return;
        }

        if (context.DryRun)
        {
            Console.WriteLine($"[{Name}] Dry-run skipped.");
            return;
        }

        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var service = CodeIndexService.TryCreate(options);
        if (service is null)
        {
            Console.WriteLine($"[{Name}] Could not create index service.");
            return;
        }

        CodeIndexAmbient.Set(service, work);
        context.Items["codeIndex"] = service;

        try
        {
            var (chunks, commit) = await service.IndexWorkspaceAsync(work, cancellationToken);
            context.IndexedChunkCount = chunks;
            context.IndexedCommitSha = commit;
            context.RetrievalReady = chunks > 0;
            Console.WriteLine(
                $"[{Name}] Indexed {chunks} chunk(s) at {commit[..Math.Min(12, commit.Length)]} "
                + $"backend={service.Backend} embedder={service.Embedder}.");
        }
        catch (Exception ex)
        {
            // Retrieval is an accelerator — never fail the whole run if Qdrant is briefly down.
            Console.Error.WriteLine($"[{Name}] Index failed (continuing without retrieval): {ex.Message}");
            context.RetrievalReady = false;
        }
    }
}
