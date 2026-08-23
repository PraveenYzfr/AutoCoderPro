# AutoCoderPro

> Large-repo Autocoding: **Jira → plan → code → PR**, with **MCP + RAG** navigation.

Forked from [AutoCoder](https://github.com/PraveenYzfr/AutoCoder) as a **separate system** (see CLAUDE-AUTOCODER.md item 11). AutoCoder stays the proven small-repo orchestrator; this repo is where retrieval, indexing, and larger-codebase agent navigation land — without destabilising AutoCoder.

| | |
|--|--|
| Status | Bootstrap — copied AutoCoder baseline; Pro capabilities TBD |
| Stack | .NET 8+ orchestrator (C#) |
| Planned additions | MCP tool servers · RAG over code (chunk on function/class boundaries) · Qdrant (already on the estate VM) |
| Boundary | Do **not** merge Pro-only retrieval/limits back into AutoCoder |

## Baseline (from AutoCoder)

Same ticket → scout → plan → agentic implement → build/test → PR → Jira writeback loop. Cross-provider fallback (`deepseek` ↔ `groq`) and run retention are already in this tree.

```bash
dotnet run --project src/AutoCoder.Cli -- dry-run --ticket samples/ticket.json
```

## Roadmap (Pro)

1. Copy-only baseline (this commit) — keep namespaces working; rename later if needed.
2. Code RAG: index on commit, semantic `search_code` tool for the coding agent.
3. MCP: optional read-only Jira/GitHub context tools; keep hand-rolled clone/commit/PR guards.
4. Raise nothing in AutoCoder — larger limits belong here.

## License

Same as AutoCoder (see `LICENSE`).
