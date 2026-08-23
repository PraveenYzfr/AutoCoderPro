# AutoCoderPro

> Large-repo Autocoding: **Jira → index → plan → code → PR**, with **RAG + MCP**.

Forked from [AutoCoder](https://github.com/PraveenYzfr/AutoCoder). AutoCoder stays the proven small-repo orchestrator; **Pro is the future** — retrieval navigation and MCP tools live here only.

| | |
|--|--|
| Status | Item 11 baseline landed — retrieval on by default (`memory` backend) |
| Stack | .NET 8+ orchestrator (C#) |
| Retrieval | Function/class chunking → memory or Qdrant index → `search_code` tool |
| MCP | Optional stdio servers; tools merge as `mcp_<server>_<tool>` |
| Estate | Qdrant already on the VM — set `retrieval.backend: qdrant` + `AUTOCODER_QDRANT_URL` |

## What changed vs AutoCoder

1. **`IndexRepo` step** after clone — chunks on function/class boundaries (imports stay with the first unit), indexed by commit SHA.
2. **Scout** prefers retrieval hits over a flat file dump when the repo is large enough.
3. **Coding agent** gets `search_code` so it finds files by meaning instead of opening them one at a time.
4. **MCP scaffold** — allowlisted stdio servers; read-only context tools only (clone/commit/PR stay hand-rolled).
5. Slightly higher Pro limits (60 tool calls / 800k tokens) — retrieval should still make tool use *more efficient*, not just bigger.

## Config

```yaml
retrieval:
  enabled: true
  backend: memory          # or qdrant
  qdrant_url: http://qdrant:6333
  embedder: deterministic  # or openai (+ EMBEDDING_API_KEY / OPENAI_API_KEY)
  top_k: 8
  large_repo_file_threshold: 40

mcp:
  enabled: false
  servers: []
  # - name: github
  #   command: npx
  #   args: ["-y", "@modelcontextprotocol/server-github"]
  #   read_only: true
```

Env overlays: `AUTOCODER_RETRIEVAL_ENABLED`, `AUTOCODER_RETRIEVAL_BACKEND`, `AUTOCODER_QDRANT_URL` / `QDRANT_URL`, `AUTOCODER_EMBEDDER`, `AUTOCODER_MCP_ENABLED`.

## Quick start

```bash
dotnet run --project src/AutoCoder.Cli -- dry-run --ticket samples/ticket.json
dotnet test
```

## License

Same as AutoCoder (see `LICENSE`).
