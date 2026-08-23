# AutoCoderPro

> Large-repo Autocoding: **Jira → index → plan → code → PR**, with **RAG + MCP**.

Forked from [AutoCoder](https://github.com/PraveenYzfr/AutoCoder). AutoCoder stays frozen at current capability; **Pro is the future**.

| | |
|--|--|
| Status | Item 11 — retrieval defaults match production |
| Default retrieval | **qdrant + gemini** (`gemini-embedding-001`, **768 dims**) |
| Lightweight opt-in | `backend: memory` + `embedder: deterministic` only when you ask for it |
| LLM cost order | DeepSeek → Groq (OpenAI/Anthropic/Gemini chat = benchmark only) |
| Embedding cost | **Gemini** (not OpenAI) — same family SeekandDestroy/B already uses |

## Why defaults are qdrant + gemini

Running `memory` + `deterministic` locally while the VM used qdrant + a real embedder is the same class of bug that cost SeekandDestroy (mock LLM shipping, clear-without-recreate invisible in-memory, 3072-dim upsert blowups). Local defaults to the production stack so those failures are reachable before deploy. Missing `GEMINI_API_KEY` **fails the index step loudly** — it does **not** silently fall back to deterministic.

## Config

```yaml
retrieval:
  enabled: true
  backend: qdrant
  qdrant_url: http://qdrant:6333
  embedder: gemini
  embedding_model: gemini-embedding-001
  embedding_dimensions: 768   # not 3072 — keeps upserts small
  top_k: 8
```

Env: `GEMINI_API_KEY`, `AUTOCODER_QDRANT_URL` / `QDRANT_URL`, optional `AUTOCODER_RETRIEVAL_BACKEND`, `AUTOCODER_EMBEDDER`.

## Local compose

`docker compose up` starts **qdrant + orchestrator** together. Set `GEMINI_API_KEY` in `.env`.

## Pipeline extras vs AutoCoder

1. `IndexRepo` after clone (commit-SHA keyed; Qdrant delete always followed by recreate; dim mismatch recreates).
2. Scout prefers retrieval hits on large repos.
3. Coding agent `search_code` tool.
4. Optional MCP stdio tools as `mcp_*`.

## License

Same as AutoCoder (see `LICENSE`).
