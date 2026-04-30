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
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode retrieval `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -ApiKey "<saaia_api_key>"

# Exercise retrieval + local OpenAI-compatible LLM.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 `
  -Mode llm `
  -BackendBaseUrl "https://saaia-server.taila2196b.ts.net" `
  -ApiKey "<saaia_api_key>" `
  -LlmBaseUrl "http://127.0.0.1:1234" `
  -LlmModel "local"
```

Outputs are written under `artifacts/llm-validation/`:

- `.json`: full summary plus rows
- `.jsonl`: one complete record per question, including sources and answer
- `.tsv`: spreadsheet-friendly review sheet

## Useful slices

```powershell
# Quick smoke.
powershell -ExecutionPolicy Bypass -File tools\run-llm-validation.ps1 -Mode plan -Limit 30

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
