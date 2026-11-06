# Recommended Local LLM Model Settings

Measured recommendations for recipe **text** ingestion (web URLs, pasted text, digital PDFs) with Gemma 4 models served by Ollama on a single 16 GB GPU. For each model it lists what to set in LocalRoast's **LLM Settings** dialog, what the Ollama server needs, and what goes wrong.

Results come from the [ATK ingestion evaluator](contributing.md#evaluating-atk-ingestion-on-a-local-model-server), which runs the production extraction client and draft normalizer without touching a database.

---

## Table of Contents

1. [Quick Recommendations](#quick-recommendations)
2. [Test Setup](#test-setup)
3. [Measured Results](#measured-results)
4. [Per-Model Notes](#per-model-notes)
5. [Ollama Server Configuration](#ollama-server-configuration)
6. [Limitations & Future Work](#limitations--future-work)

---

## Quick Recommendations

| Text Model | Recommendation | Thinking | Timeout (s) | Context window | Auto-publish |
| --- | --- | --- | --- | --- | --- |
| `gemma4:e4b` | **Default choice** | On | 600 | 16,384 | Off |
| `gemma4:e2b` | Fast drafts; review every recipe | On | 300 | 16,384 | Off |
| `gemma4:12b` | About 2x faster than E4B, slightly more findings | **Off** | 300 | 16,384 | Off |
| `gemma4:26b-a4b` | Not recommended on 16 GB | Off | 300 | 16,384, plus partial GPU offload on the server | Off |

- **Context window:** LocalRoast sends the **Context window** from LLM Settings (default 8,192) with every Ollama request, for every model. Raise it to 16,384 for these models: E4B with thinking used up to 16,381 tokens, and E2B up to 13,096. Larger values are untested and use more GPU memory.
- **Keep-alive:** the default `5m` keeps the model loaded for the back-to-back requests of a single job and for consecutive queued jobs. Raise it (for example `30m`) only if the queue has long gaps. Models loaded in about 9–16 seconds on the test server.
- **Auto-publish:** keep it off for all of these models. The quality gate already blocks drafts with detectable problems, but zero detected issues does not prove the recipe is correct.
- **Thinking applies to the Vision Model too.** The toggle is household-wide, and vision extraction also uses temperature 0. Do not enable thinking while either the Text or Vision Model is `gemma4:12b` (see [gemma4:12b](#gemma412b)).

---

## Test Setup

- **Hardware:** NVIDIA RTX 2000 Ada (16 GB VRAM) with 64 GB of system RAM.
- **Server:** Ollama 0.40.13 using its Vulkan backend, limited to one concurrent request and one loaded model. The CUDA backend may fit models and perform differently; it was not tested.
- **Cases:** eight ATK recipes from `scripts/atk_ingestion_cases.json`: four tuning cases, three held-out cases and one multi-day stress case. E2B and E4B ran all eight. The larger models were probed on the three cases E4B handled worst:
  - `c006-s368`: divided chicken and oil, plus short sauté timers.
  - `c002-s84`: separated eggs and a fractional flour amount.
  - `c014-s1134`: butter and flour with extra portions for the pan, plus bake and cool timers.
- **Score:** *findings* are evaluator assertion failures (lower is better). Examples are wrong totals or splits, amounts left in step text, invalid ingredient links, missing timers and failed extractions. Each larger-model configuration ran once, so expect run-to-run variation, especially with sampling.
- **Code version:** measured at commit `8eb9ac3`, before per-step `ingredientAmounts`, `alternativeMeasurement`, the configurable context window and the CPU-only option were merged. Those changes altered the extraction prompts and schema, so re-run the evaluator to refresh these numbers.

---

## Measured Results

### All eight cases (measured at `8eb9ac3`)

| Model | Findings | Zero-finding cases | Mean time per recipe |
| --- | --- | --- | --- |
| `gemma4:e2b` (thinking) | 31 | 1 of 8 | 47 s |
| `gemma4:e4b` (thinking) | 13 | 3 of 8 | 125 s |

E4B's stress case failed cleanly at the output-token limit after 219 seconds instead of publishing a partial recipe.

### Three hardest cases

| Configuration | Available in LocalRoast today? | Findings | Time per recipe |
| --- | --- | --- | --- |
| `gemma4:e2b`, thinking, staged | Yes | 16 | ~51 s |
| `gemma4:e4b`, thinking, staged | Yes | 11 | ~131 s |
| `gemma4:12b`, thinking, temperature 0 | Yes, but fails | Failed 2 of 2 | ~5.4 min, then failed |
| `gemma4:12b`, no thinking | Yes | 14 | ~50–70 s |
| `gemma4:12b`, thinking + Gemma sampling | **No** (benchmark only) | **8** | ~4.5 min |
| `gemma4:26b-a4b`, no thinking | Only with server-side partial GPU offload | 17 | ~47 s |
| `gemma4:26b-a4b`, thinking + Gemma sampling | **No** (benchmark only) | 10 | ~4.3 min |

*Staged* means LocalRoast's separate ingredient and instruction requests followed by at most one correction request. *Gemma sampling* is temperature 1.0, `top_p` 0.95 and `top_k` 64.

---

## Per-Model Notes

### `gemma4:e4b`

- **Set:** Text Model `gemma4:e4b`, thinking on, timeout 600 seconds, context window 16,384, keep-alive `5m`, auto-publish off.
- **LocalRoast sets automatically:** staged extraction (up to three requests per recipe), temperature 0 and repetition penalty 1. The timeout applies to each request, not the whole job. The longest single request measured was 108 seconds; 600 leaves headroom for long recipes and busy servers.
- **Weaknesses:**
  - amounts left in step text
  - divided ingredients that are not split
  - invalid ingredient links
  - wrong split totals (separated eggs, butter reserved for the pan)
  - missing bake and cool timers
- **Long recipes:** very long recipes can use the whole 16,384-token context while thinking and fail. Those jobs fail cleanly and can be retried or entered manually. A larger context window may let them finish, but it was not tested and needs more GPU memory.

### `gemma4:e2b`

- **Set:** the same as E4B, but a 300-second timeout is enough (the longest request measured was 42 seconds).
- **Tradeoff:** about 2.5 times faster than E4B, with more than twice as many findings.
- **Weaknesses:**
  - amounts in step text
  - missed splits
  - alternatives added as extra ingredients
  - fraction errors, such as reading 2/3 cup as 2.667
- **Use it** when speed matters and you will review every recipe.

### `gemma4:12b`

- Fits entirely in 16 GB of VRAM at a 16,384-token context and generated about 36–43 tokens per second.
- **Do not enable thinking in LocalRoast yet.** LocalRoast sends temperature 0 for extraction. With thinking enabled, the model repeated the same reasoning until it hit the output limit and failed (2 of 2 cases, about 5.4 minutes each).
- **With thinking off:** single-pass extraction, 14 findings on the three hard cases (versus 11 for E4B) in about a minute per recipe. Its most common problem was invalid ingredient links, which the quality gate flags for review.
- **Set the context window to 16,384.** These requests used up to 5,900 tokens without thinking and 14,000 with it, so the default 8,192 leaves little headroom.
- **Best measured configuration:** thinking with Gemma sampling had 8 findings and produced a perfect bundt cake (E4B's worst case), using one request in about 4.5 minutes. LocalRoast cannot use it yet:
  - LocalRoast always sends temperature 0, which overrides any `PARAMETER temperature` in a Modelfile.
  - It needs per-model sampling settings in LocalRoast.

### `gemma4:26b-a4b`

- **Memory:** the 17.4 GB mixture-of-experts model does not fit in 16 GB of VRAM. On the test server it failed to load with a GPU out-of-memory error. Requesting `num_gpu` 24 kept 24 layers on the GPU, ran the rest from system RAM, and loaded in 16 seconds; all measurements above used this setting. LocalRoast only sends `num_gpu` for **Run Ollama on CPU only** (`num_gpu` 0), so partial offload must be set in a derived model (see below). CPU-only mode also loads the model, but runs entirely from system RAM and was not benchmarked for extraction.
- **Not recommended:**
  - Without thinking it had the most findings (17).
  - With thinking and sampling it had 10 findings, but it kept one recipe's four long method paragraphs as steps of up to 1,100 characters. The other models split the same method into 16–20 focused steps, which the serving multiplier and timers depend on. It was also no faster than 12b.
  - Thinking at temperature 0 was not tested; expect the same looping risk as 12b.

---

## Ollama Server Configuration

- **One request and one model at a time:** set `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1` on smaller GPUs so ingestion requests never compete for VRAM.
- **Context:** set it in LocalRoast's LLM Settings. The `num_ctx` LocalRoast sends with each request overrides `OLLAMA_CONTEXT_LENGTH` and any Modelfile `num_ctx`.
- **Partial GPU offload for `gemma4:26b-a4b`:** create a derived model and select it as the Text Model, leaving **Run Ollama on CPU only** off (when on, LocalRoast's `num_gpu` 0 overrides the Modelfile):

  ```text
  # Modelfile
  FROM gemma4:26b-a4b
  PARAMETER num_gpu 24
  ```

  ```powershell
  ollama create gemma4-26b-recipes -f Modelfile
  ```

  This was verified only through the API's `options`, not through a derived model inside LocalRoast.
- **Keep E2B/E4B tag names.** LocalRoast's staged extraction only applies to Text Model tags that begin with `gemma4:e2b` or `gemma4:e4b`. A copy of either model under a different name falls back to single-pass extraction.
- **Check GPU placement:** run `ollama ps`. `100% GPU` means the model is fully on the GPU; a CPU/GPU split means part of it runs from system RAM and is slower.

---

## Limitations & Future Work

- These are small, single-run samples from one cookbook format. Re-run the evaluator on your own hardware before changing household defaults, and keep reviewing recipes with separated eggs, prepared mixtures, alternatives and repeated batches.
- Vision models (cookbook photos and scanned PDFs) were not benchmarked.
- To test configurations LocalRoast cannot express yet, pass explicit request overrides to the evaluator. They are recorded with every result and never change LocalRoast or server defaults:

  ```powershell
  python scripts\evaluate_atk_ingestion.py --base-url http://localhost:11434 --thinking `
    --models gemma4:12b --ids c006-s368 c002-s84 c014-s1134 `
    --ollama-options '{"num_ctx":16384,"temperature":1.0,"top_p":0.95,"top_k":64}' `
    --timeout 600 --output "C:\PrivateBenchmarks\atk-12b-sampled"
  ```

- Planned improvements: per-model profiles (staged mode, thinking, sampling preset and GPU layers) instead of tag-name checks and household-wide settings, and an iterative, issue-targeted repair loop. Together these would make the best measured `gemma4:12b` configuration available in LocalRoast.
