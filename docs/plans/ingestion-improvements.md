# Ingestion Quality Improvements

Tracking plan for making LLM recipe ingestion reliable across every channel (web URLs, pasted text, PDFs, photos), not only well-formatted cookbook text. The guiding principle is to prefer model-driven organization and repair loops over format-specific code heuristics. Existing heuristics stay in place, but new work should not add source-format-specific rules.

See [LLM Model Settings](../llm-model-settings.md) for the per-model recommendations these items build on.

## Batch 1 — Source structure, repair loop, and output limits

- [x] **1. LLM source sectioning for every text channel.** Pasted text, digital PDFs, and web pages without complete recipe JSON-LD rarely have the canonical `Ingredients:` / `- line` / `Instructions:` layout. Without it, the source-line diagnostics (verbatim ingredient lines, conserved totals, missing timers, prepared mixtures) are skipped. Ask the text model to label every numbered source line (title, detail, description, note, ingredient group, ingredient, wrapped continuation, instruction, skip), correct common labeling mistakes with line-shape rules, and build a canonical copy from the original lines in code, so nothing is reworded or invented. Hard-wrapped prose and numbered steps are rejoined so an amount or duration is never split across lines (on the hard-wrapped bundt cake, E4B averaged 3 findings with rejoined text versus 7 without). Store it as `IngestionJob.StructuredSourceText`, and use it for extraction and diagnostics while the review screen keeps the original source. (A first version that asked for line ranges per section mislabeled wrapped and split lines too often; per-line labels replaced it.)
- [x] **2. Repair pass for every model.** Validation and correction currently run only for `gemma4:e2b`/`gemma4:e4b` tags. Run the diagnostics and correction after every text extraction, regardless of model.
- [x] **3. Iterative repair loop.** Replace the single correction request with a configurable number of rounds (household setting). Keep the draft with the fewest issues, stop as soon as a round does not improve, and fall back to the best draft when a correction response is unusable or times out.
  - Validation issues now quote what they refer to: the full text of a missing source line, the step's fixed amounts, the ingredient's name and the timed sentences. Before, E2B was told "Source ingredient line 6: missing" without the text and returned the same draft. Unlinked ingredients also name the steps that mention them. Replay results are in [Repair rounds](../llm-model-settings.md#repair-rounds-removed).
  - [ ] Follow-up: E2B still restores rows without linking them, and fixed amounts left in step text are rarely fixed (3 of 30). A small review-screen hint sanity check also left fixed amounts and introduced other mistakes; evaluate more examples and improve the repair before relying on E2B.
- [x] **4. Output cap follows the context window.** Ollama requests used a fixed 16,384-token `num_predict`, which truncated long thinking outputs even with a 32k context. Use the larger of 16,384 and the configured context window for text and vision extraction. Keep OpenAI-compatible requests at 16,384, because those servers can reject `max_tokens` larger than the remaining context.
- [x] **4a. Request timeout limit.** The guide recommends 900 s for slow thinking models, but the API and dialog capped the timeout at 600 s. Raise the limit (now 1,800 s) so long-context, thinking, and repair-loop requests can finish.
- [x] **9. Non-canonical evaluation sources (alongside batch 1).** Add evaluator source styles that remove headings and bullets, insert page noise, and hard-wrap lines. This shows whether sectioning and repair hold up without the canonical layout. The evaluator's `--source-style` option now offers `canonical`, `plain` (pasted notes) and `web` (page noise, bullets, numbered steps, 72-character wraps). Results are in [Source sectioning](../llm-model-settings.md#source-sectioning).
  - [ ] Follow-up coverage: metric, PDF, photo and handwritten samples. Vision has not been benchmarked yet.

## Batch 2 — Review experience

- [x] **5. Review-screen issues and "Fix with AI".** Show the validation issues on the review screen instead of only in the status text. Add a "Fix with AI" action that accepts an optional user hint, runs the repair loop against the stored structured source, and shows a before/after diff before applying.
  - Repair previews use the unsaved editor draft, preserve review metadata when it can be matched safely, and require a separate save or publish. Photos and scanned PDFs use the configured vision model and original images. Stale previews are rejected, and an unusable response can be retried once.
- [x] **6. Format-agnostic quality checks.** Add diagnostics that do not depend on source layout:
  - very long or multi-action steps;
  - vague timer labels (for example "Timer" or "Cook");
  - ingredients mentioned in step prose but not linked;
  - steps that reference an unknown ingredient index.
  - Quality warnings are advisory and do not change existing automatic publication gating.

## Batch 3 — Model strategy

- [x] **7. Per-model profiles.** Store recommended sampling, thinking, context, staged-extraction, and repair-round defaults per model tag. Applying a profile then becomes one click, and the staged-extraction tag check can move into data.
- [x] **8. Optional refine pass with a larger model.** Allow a second, slower model (for example Gemma 4 26B with partial offload) to refine drafts that still have issues after the primary model's repair loop. This can run in the background without blocking the queue.

## Batch 4 — Learning and investigation

- [ ] **10. Learn from user edits at publish.** Record the differences between the extracted draft and the published recipe (renamed foods, moved preparation notes, split steps, relabelled timers). Use them as evaluation data and as candidate few-shot examples.
- [ ] **11. Investigate the E2B regression.** E2B findings rose from 31 to 43 between benchmark runs. Bisect prompt and setting changes to find the cause before tuning E2B further. With the batch 1 code, E2B scored 35–36 in all three source styles, and identical runs vary by about 5–10 findings, so part of the rise may be noise. Compare with repeated runs or replayed requests rather than single runs.
