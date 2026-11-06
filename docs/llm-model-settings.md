# Recommended Local LLM Model Settings

Measured recommendations for recipe **text** ingestion (web URLs, pasted text, digital PDFs) with Gemma 4 models served by Ollama on a single 16 GB GPU. For each model it lists what to set in LocalRoast's **LLM Settings** dialog (including the optional **Sampling** fields), what the Ollama server needs, and what goes wrong.

Results come from the [ATK ingestion evaluator](contributing.md#evaluating-atk-ingestion-on-a-local-model-server), which runs the production extraction client and draft normalizer without touching a database.

---

## Table of Contents

1. [Quick Recommendations](#quick-recommendations)
2. [Sampling Settings](#sampling-settings)
3. [Test Setup](#test-setup)
4. [Measured Results](#measured-results)
5. [Per-Model Notes](#per-model-notes)
6. [Ollama Server Configuration](#ollama-server-configuration)
7. [Limitations & Future Work](#limitations--future-work)

---

## Quick Recommendations

| Text Model | Recommendation | Thinking | Temperature | Top P | Top K | Context window | Timeout (s) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `gemma4:e4b` | **Default choice** | On | Blank | Blank | Blank | 32,768 | 600 |
| `gemma4:e2b` | Fast drafts; review every recipe | On | Blank | Blank | Blank | 32,768 | 300 |
| `gemma4:12b` | Works, but less accurate than E4B | **Off** | Blank | Blank | Blank | 32,768 | 300 |
| `gemma4:12b` with thinking | Experimental | On | **1** | **0.95** | **64** | 32,768 | 900 |
| `gemma4:26b-a4b` | Not recommended on 16 GB; needs a derived model with `num_gpu 24` | On | **1** | **0.95** | **64** | 32,768 | 900 |

*Blank* means leave the field empty so LocalRoast uses its defaults (see [Sampling Settings](#sampling-settings)). For every model, keep **Auto-publish** off and keep-alive at `5m`.

- **Context window:** LocalRoast sends the **Context window** from LLM Settings (default 8,192) with every Ollama request. Use 32,768 for these models. At 16,384, E2B, E4B and 12B with thinking all failed the long multi-day stress recipe because the prompt plus the model's output filled the window. At 32,768 they finished it, and generated just as fast as at 16,384 (they still fit entirely on the 16 GB GPU). Each request's output stays capped at 16,384 tokens whatever the context window; `gemma4:26b-a4b` with thinking failed the stress recipe at that cap.
- **Keep-alive:** the default `5m` keeps the model loaded for the back-to-back requests of a single job and for consecutive queued jobs. Raise it (for example `30m`) only if the queue has long gaps. Models loaded in about 9–16 seconds on the test server.
- **Auto-publish:** keep it off for all of these models. The quality gate already blocks drafts with detectable problems, but zero detected issues does not prove the recipe is correct.
- **These settings are household-wide.** Thinking, Sampling and Context window apply to every LLM request: the Vision Model, ingredient substitutions, kitchen questions and meal-plan suggestions as well as text ingestion.

---

## Sampling Settings

LLM Settings has three optional fields under **Sampling (optional)**: **Temperature**, **Top P** and **Top K**.

- **Blank (recommended unless the table says otherwise):** LocalRoast's defaults.
  - Recipe extraction (text, staged, correction and vision requests) uses temperature 0, so the same source produces the same draft.
  - Substitutions, kitchen questions and meal-plan suggestions use temperature 0.2.
  - Top P and Top K are not sent, so the model's own defaults apply. Every Gemma 4 tag in the Ollama library ships with Top P 0.95 and Top K 64; at temperature 0 they have no effect.
- **Set:** each value is sent with every request, for both Ollama and OpenAI-compatible providers. Clear a field to return to the default. Some hosted OpenAI-compatible APIs reject Top K; leave it blank for those.
- **Why `gemma4:12b` with thinking needs them:** at temperature 0 it repeated the same reasoning until it hit the output limit and failed (2 of 2 recipes). Temperature 1, Top P 0.95 and Top K 64 (the defaults the Gemma 4 models ship with) fix that.
- **A Modelfile cannot replace the Temperature field.** LocalRoast always sends a temperature, which overrides `PARAMETER temperature`. Modelfile Top P and Top K do apply while those fields are blank, so on Ollama Temperature 1 alone gives Gemma 4 its full default sampling. Set all three anyway so the profile also works on servers with different defaults.
- **Side effects of setting them:**
  - Extraction is no longer repeatable: importing the same recipe twice can give different drafts. In two `gemma4:12b` runs, one pancake recipe came back as 16 focused steps and then as 3 long paragraphs.
  - Substitutions, kitchen answers and Vision Model extraction use the same values.
- **They did not help E2B or E4B.** With Gemma sampling, E2B had 39 findings instead of 43 (within run-to-run noise) and E4B had 12 instead of 10, plus one extra failed recipe. Keep them blank for these models.

---

## Test Setup

- **Hardware:** NVIDIA RTX 2000 Ada (16 GB VRAM) with 64 GB of system RAM.
- **Server:** Ollama 0.40.13 using its Vulkan backend, limited to one concurrent request and one loaded model. The CUDA backend may fit models and perform differently; it was not tested.
- **Cases:** eight ATK recipes from `scripts/atk_ingestion_cases.json`: four tuning cases, three held-out cases and one multi-day stress case (`c007-s540`, Beef Wellington). Every configuration below ran all eight. The three cases E4B handled worst in earlier rounds are called out separately:
  - `c006-s368`: divided chicken and oil, plus short sauté timers.
  - `c002-s84`: separated eggs and a fractional flour amount.
  - `c014-s1134`: butter and flour with extra portions for the pan, plus bake and cool timers.
- **Score:** *findings* are evaluator assertion failures (lower is better). Examples are wrong totals or splits, amounts left in step text, invalid ingredient links, missing timers and failed extractions; a failed recipe counts as one finding. Findings do not measure how well the method is split into steps, so the results also count steps and steps over 300 characters: long steps combine several actions, which makes divided ingredients and timers harder to follow. Each configuration ran once. Blank sampling (temperature 0) is repeatable; runs with sampling set are not, so expect run-to-run variation.
- **Code version:** measured on the branch that added the Sampling settings, which includes per-step `ingredientAmounts`, `alternativeMeasurement`, the configurable context window and the CPU-only option. Earlier numbers from commit `8eb9ac3` are only quoted for comparison.
- **Context window:** the sampling comparison ran at 16,384 tokens. Recipes that failed there were re-run at 32,768; `gemma4:12b` without thinking and `gemma4:26b-a4b` ran at 32,768 throughout. Recipes that finished at 16,384 were not re-run: the window only matters once it fills, and at temperature 0 the output did not change (E4B's first two stress-recipe requests matched token for token at both sizes).

---

## Measured Results

### All eight cases at the recommended context window (32,768)

| Configuration | Findings | Zero-finding cases | Failed recipes | Steps (over 300 characters) | Mean time per recipe | Longest request |
| --- | --- | --- | --- | --- | --- | --- |
| `gemma4:e4b`, thinking, blank sampling | **10** | **4 of 8** | 0 | 79 (15) | 160 s | 119 s |
| `gemma4:26b-a4b`, thinking, Gemma sampling | 19 | 2 of 8 | 1 | 59 (8) in 7 recipes | 325 s | 422 s |
| `gemma4:12b`, no thinking, blank sampling | 28 | 1 of 8 | 0 | 102 (9) | 90 s | 177 s |
| `gemma4:26b-a4b`, no thinking, blank sampling | 32 | 2 of 8 | 0 | 51 (23) | 75 s | 141 s |
| `gemma4:12b`, thinking, Gemma sampling | 33 | 2 of 8 | 0 | 85 (15) | 254 s | 430 s |
| `gemma4:e2b`, thinking, blank sampling | 49 | 1 of 8 | 0 | 113 (7) | 65 s | 57 s |

*Gemma sampling* is Temperature 1, Top P 0.95 and Top K 64. `gemma4:26b-a4b` used a 24-layer GPU offload (see [its notes](#gemma426b-a4b)). On the three hardest cases, E4B had 6 findings; `gemma4:26b-a4b` had 9 with thinking and 14 without; `gemma4:12b` had 10 with thinking and 15 without.

### Sampling comparison (16,384-token context)

| Model and thinking | Sampling | Findings | Zero-finding cases | Failed recipes | Mean time per recipe |
| --- | --- | --- | --- | --- | --- |
| `gemma4:e4b`, thinking | Blank | **10** | 4 of 8 | 1 | 153 s |
| `gemma4:e4b`, thinking | Gemma | 12 | 2 of 8 | 2 | 157 s |
| `gemma4:e2b`, thinking | Blank | 43 | 1 of 8 | 1 | 63 s |
| `gemma4:e2b`, thinking | Gemma | 39 | 0 of 8 | 1 | 70 s |
| `gemma4:12b`, thinking | Gemma | 23 | 2 of 8 | 2 | 248 s |
| `gemma4:12b`, thinking | Blank (temperature 0) | — | — | 2 of 2 tried (at `8eb9ac3`) | ~5.4 min |

Every failure here was the output-token limit: the prompt and output filled the 16,384-token window.

### Context window on long recipes

| Recipe | Configuration | Peak tokens (prompt + output) | At 16,384 | At 32,768 |
| --- | --- | --- | --- | --- |
| Lemon bundt cake (`c014-s1134`) | E4B, blank | 15,853 | Finished (0 findings), 531 tokens to spare | Not needed |
| Lemon bundt cake | 12B, thinking, Gemma sampling | 13,919–16,381 | Failed | Finished, 1 finding |
| Beef Wellington (`c007-s540`) | E2B, blank | 18,240 | Failed | Finished, 7 findings |
| Beef Wellington | E4B, blank | 20,410 | Failed | Finished, 1 finding |
| Beef Wellington | 12B, thinking, Gemma sampling | 19,567 | Failed | Finished, 11 findings |
| Beef Wellington | 26B, thinking, Gemma sampling | 21,256 | Not run | Failed: output reached the 16,384-token cap |

Generation speed did not change between the two sizes: about 150–160 tokens per second for E2B, 91–100 for E4B and 35–36 for 12B. Partly offloaded, 26B generated 39–42 tokens per second with thinking and 46–49 without. The largest successful output was 15,707 tokens (26B with thinking, shrimp risotto), close to LocalRoast's 16,384-token output cap.

### Changes since `8eb9ac3`

- E4B improved from 13 to 10 findings on all eight cases.
- E2B got worse, from 31 to 43 findings at the same 16,384-token context. Most of the increase is in the shrimp risotto (17 findings: amounts left in step text and rewritten source lines) and the bundt cake (9: split totals for the pan portions).
- 12B with thinking and Gemma sampling had 8 findings on the three hardest cases then and 10 now.

---

## Per-Model Notes

### `gemma4:e4b`

- **Set:** Text Model `gemma4:e4b`, thinking on, Sampling blank, context window 32,768, timeout 600 seconds, keep-alive `5m`, auto-publish off.
- **LocalRoast sets automatically:** staged extraction (separate ingredient and instruction requests, then at most one correction request), temperature 0 while Sampling is blank, and repetition penalty 1. The timeout applies to each request, not the whole job. The longest single request measured was 119 seconds; 600 leaves headroom for longer recipes and busy servers.
- **Weaknesses:**
  - divided ingredients that are not split (chicken and oil cooked in batches)
  - wrong split totals for separated eggs
  - amounts left in step text (3 of 10 findings)
  - missing preparation notes and a dropped lime juice row
  - some long steps: 15 of 79 were over 300 characters, the longest 939 (bundt cake). Findings do not count these.
- **Long recipes:** the stress recipe's correction request used 20,410 tokens. It failed at 16,384 and finished at 32,768 with one finding.

### `gemma4:e2b`

- **Set:** the same as E4B, but a 300-second timeout is enough (the longest request measured was 57 seconds).
- **Tradeoff:** about 2.5 times faster than E4B, with about five times as many findings.
- **Weaknesses:**
  - amounts left in step text (11 of 49 findings)
  - source ingredient lines rewritten instead of copied verbatim
  - wrong split totals (butter and flour for the pan, separated eggs)
  - missing timers and ingredients not linked to any step
- **Use it** when speed matters and you will review every recipe.

### `gemma4:12b`

- **Fits entirely in 16 GB of VRAM** at a 32,768-token context and generated about 35–36 tokens per second. It always gets a single request per recipe: LocalRoast's staged extraction and correction request only run for `gemma4:e2b` and `gemma4:e4b` tags, so nothing repairs its mistakes. Its most common finding was amounts left in step text, which that correction request targets.
- **Recommended: thinking off, Sampling blank,** context window 32,768, timeout 300 seconds. 28 findings, 90 seconds per recipe on average (longest request 177 seconds) and repeatable output. It split methods into focused steps (102 steps, 9 over 300 characters), but did not beat E4B on findings.
- **Thinking on needs Temperature 1, Top P 0.95 and Top K 64.** At temperature 0 it repeated the same reasoning until it hit the output limit. With those values it worked:
  - 33 findings in about 4.2 minutes per recipe. It did best on the three hardest cases (10 findings versus 15 without thinking), but worse on the rest.
  - Use a 900-second timeout (one request took 430 seconds) and the 32,768-token context (two recipes failed at 16,384).
  - Output changes from run to run, and the household-wide sampling also applies to helpers and the Vision Model.

### `gemma4:26b-a4b`

- **Memory:** the 17.4 GB mixture-of-experts model does not fit in 16 GB of VRAM. On the test server it failed to load with a GPU out-of-memory error. A derived model with `num_gpu` 24 (see [Ollama Server Configuration](#ollama-server-configuration)) keeps 24 layers on the GPU and runs the rest from system RAM; it loaded in 16 seconds. All 26B measurements passed `num_gpu` 24 through the evaluator. CPU-only mode also loads the model, but runs entirely from system RAM and was not benchmarked for extraction.
- **Speed:** only about 4 billion of its parameters are active for each token, so even partly offloaded it generated 39–49 tokens per second, faster than 12B.
- **Single request:** like 12B it gets one request per recipe and no correction request. Amounts left in step text were 10 of its 19 findings with thinking and 9 of 32 without.
- **With thinking (Temperature 1, Top P 0.95, Top K 64):** the most accurate configuration after E4B, with 19 findings and 9 on the three hardest cases (E4B: 6). However:
  - It averaged 5.4 minutes per recipe, twice E4B's time, and produced 8,100–16,400 output tokens per recipe, mostly reasoning.
  - It failed the stress recipe when its output reached LocalRoast's fixed 16,384-token output cap, with about 11,500 tokens of the 32,768-token context still free. A larger context window does not help.
  - It split the shrimp risotto into 5 steps and the mashed potatoes into 3; the other models produced 12–16 and 6–9.
  - Use a 900-second timeout (the longest request took 422 seconds). Thinking at temperature 0 was not tested; expect the same looping as 12B.
- **Without thinking (Sampling blank):** 75 seconds per recipe and no failures, but 32 findings, and it mostly copied the source's method paragraphs as steps: 51 steps in total, 23 of them over 300 characters. The pancakes became 3 steps where E2B, E4B and 12B without thinking produced 15–18.
- **Not recommended on a 16 GB GPU:** it needs a derived model, and with thinking it is twice as slow as E4B, less accurate and can fail long recipes. Revisit it with more VRAM, or if LocalRoast raises the output cap or runs the correction request for every model.

---

## Ollama Server Configuration

- **One request and one model at a time:** set `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1` on smaller GPUs so ingestion requests never compete for VRAM.
- **Context:** set it in LocalRoast's LLM Settings. The `num_ctx` LocalRoast sends with each request overrides `OLLAMA_CONTEXT_LENGTH` and any Modelfile `num_ctx`.
- **Sampling:** set Temperature in LocalRoast's LLM Settings; LocalRoast always sends one, so a Modelfile `PARAMETER temperature` has no effect. Modelfile `top_p` and `top_k` apply only while those LLM Settings fields are blank (see [Sampling Settings](#sampling-settings)).
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
- **Check GPU placement:** run `ollama ps`. `100% GPU` means the model is fully on the GPU; a CPU/GPU split means part of it runs from system RAM and is slower. On the Vulkan test server `ollama ps` reported implausibly small sizes, so a drop in tokens per second after changing the context window was the more reliable sign.

---

## Limitations & Future Work

- These are small, single-run samples from one cookbook format. Re-run the evaluator on your own hardware before changing household defaults, and keep reviewing recipes with separated eggs, prepared mixtures, alternatives and repeated batches.
- Vision models (cookbook photos and scanned PDFs) were not benchmarked.
- The evaluator's `--temperature`, `--top-p`, `--top-k` and `--context-window` flags mirror the LLM Settings fields. For options LocalRoast cannot express, such as `num_gpu` for `gemma4:26b-a4b`, add `--ollama-options`. Both are recorded with every result and never change LocalRoast or server defaults:

  ```powershell
  python scripts\evaluate_atk_ingestion.py --base-url http://localhost:11434 --thinking `
    --models gemma4:12b --ids c006-s368 c002-s84 c014-s1134 `
    --temperature 1 --top-p 0.95 --top-k 64 --context-window 32768 `
    --timeout 900 --output "C:\PrivateBenchmarks\atk-12b-sampled"
  ```

- Planned improvements:
  - Run the correction request for every model, not only `gemma4:e2b`/`gemma4:e4b` tags. Amounts left in step text were the most common `gemma4:12b` finding, and that request targets them.
  - Per-model profiles (staged mode, thinking, sampling and GPU layers) instead of tag-name checks and household-wide settings, so helpers and the Vision Model do not inherit a text model's sampling.
  - Let the output cap grow with the context window. Thinking models spend most of their output on reasoning: `gemma4:26b-a4b` failed the stress recipe at the fixed 16,384-token cap, and its successful recipes used up to 15,707 tokens.
  - Find the cause of E2B's regression since `8eb9ac3`.
  - An iterative, issue-targeted repair loop: more than one correction request, or a repair requested from the review screen.
