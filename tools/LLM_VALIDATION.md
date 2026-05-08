# LLM validation campaigns

`run-llm-validation.ps1` runs large question banks against the SAAIA retrieval and/or local LLM stack.

The default bank is:

```text
backend/SAAIA.Backend.Tests/Fixtures/retrieval_cuisine_validation.v1.json
```

It currently contains 294 cuisine questions covering corpus inventory, single-recipe extraction, sourcing, ambiguity, weekly menu planning, substitutions, multi-turn memory, multi-document synthesis, and anti-hallucination/refusal behavior.

## Modes

```powershell
# Prepare a TSV/JSON/JSONL campaign without calling backend or LLM.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode plan

# Exercise backend retrieval only.
$env:SAAIA_API_KEY = "<saaia_api_key>"
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode retrieval `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net"

# Exercise retrieval + local OpenAI-compatible LLM.
$env:SAAIA_API_KEY = "<saaia_api_key>"
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode llm `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -LlmBaseUrl "http://127.0.0.1:1234" `
  -LlmModel "local"

# Exercise the real client agent pipeline: router, tools, RAG safeguards,
# deterministic source-backed answers, and local OpenAI-compatible LLM.
$env:SAAIA_API_KEY = "<saaia_api_key>"
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode agent `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -LlmBaseUrl "http://127.0.0.1:1234" `
  -LlmModel "Qwen2.5-3B-Instruct-Q4_K_M.gguf" `
  -Category "Cuisine"
```

Use `agent` mode for product-quality validation. It runs each selected question through `RagChatAgent`, so it catches routing, category scope, memory/source repair, deterministic fallbacks, and writer behavior. Use `llm` mode only as a lower-level model probe; it does not represent the full client behavior.

Outputs are written under `artifacts/llm-validation/`:

- `.json`: full summary plus rows
- `.jsonl`: one complete record per question, including sources and answer
- `.tsv`: spreadsheet-friendly review sheet

The TSV includes `answerFlags` for quick triage. Current automatic flags catch internal validation leaks, invented web links for local documents, repetitive loops, suspicious scaling answers, explicit exclusion terms that reappear, and likely degenerate output. These flags are conservative review aids, not a final quality grade.

The TSV/JSON also includes retrieval diagnostics for the selected sources:

- `top1DocHit` / `top3DocHit`: whether the first/top-three sources match a single expected document target when the question declares one.
- `distinctDocCount`: number of distinct documents returned.
- `navigationTop1` / `navigationReturned`: whether table-of-contents/index-like chunks are dominating retrieval.
- `top1ContentRole`, `top1NavigationScore`, `top1ContentDensityScore`: backend content-shape signals propagated into the validation output.
- `queryExpansionUsed` and `retrievalQueryCount`: whether the validation harness used extra diagnostic queries. A high success rate that depends heavily on expansions is a warning that the product runtime still needs improvement.

In LLM mode, the runner also injects deterministic helper facts when it can derive them from retrieved sources:

- quantity scaling facts, for example `Pour 4 personnes -> 12 personnes`, factor `x3`, and scaled numeric quantities
- exclusion facts, for example `sans fromage` when all retrieved excerpts still contain `fromage`

This mirrors the product direction: let deterministic code handle fragile arithmetic and hard constraints, then let the LLM phrase the answer.

## Useful slices

```powershell
# Quick smoke.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode plan -Limit 30

# Retrieval campaign in parallel, useful for the full 294-question cuisine bank.
$env:SAAIA_API_KEY = "<saaia_api_key>"
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode retrieval `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -Parallelism 8

# Runtime-like retrieval: no diagnostic title/ingredient/step query expansion.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode retrieval `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -Category "Cuisine" `
  -DisableDiagnosticQueryExpansion `
  -Parallelism 8

# Resume a slice without changing the bank.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode plan `
  -Offset 100 `
  -Limit 50

# Weekly/menu planning style prompts.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode plan `
  -Axis "Composition de menu / fusion contrôlée;Raisonnement multi-doc complexe;Quantités / liste de courses / planning"

# Anti-hallucination and refusal cases.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode plan `
  -Axis "Robustesse / refus / hallucination"

# Hard cases only.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode plan `
  -Difficulty "Difficile;Très difficile"

# Specific questions.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode plan `
  -Ids Q207,Q219,Q220

# Product smoke through the real client agent.
$env:SAAIA_API_KEY = "<saaia_api_key>"
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode agent `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -LlmBaseUrl "http://127.0.0.1:1234" `
  -LlmModel "Qwen2.5-3B-Instruct-Q4_K_M.gguf" `
  -Category "Cuisine" `
  -Ids Q030,Q078,Q123,Q159,Q181,Q292 `
  -TimeoutSeconds 300 `
  -MaxLlmTokens 900
```

## Adding another category

Create another fixture with the same shape:

```json
{
  "version": "category-v1",
  "generatedAt": "2026-04-30",
  "sourceFile": "source-file-name.csv",
  "description": "What this pack validates.",
  "validationCases": [
    {
      "id": "Q001",
      "axis": "Inventory",
      "difficulty": "Easy",
      "corpusTarget": "All",
      "theme": "Corpus overview",
      "question": "User-style question.",
      "expectedAnswerKind": "Expected behavior, not a fixed answer.",
      "validationPoints": "Manual/automatic checks."
    }
  ]
}
```

Then run:

```powershell
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -QuestionBankPath path\to\new_category_validation.v1.json `
  -Mode plan
```
