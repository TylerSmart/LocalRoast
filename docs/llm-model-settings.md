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
6. [Refine Model (background second pass)](#refine-model-background-second-pass)
7. [Hosted OpenAI-Compatible Providers (OpenRouter)](#hosted-openai-compatible-providers-openrouter)
8. [Ollama Server Configuration](#ollama-server-configuration)
9. [Limitations & Future Work](#limitations--future-work)

---

## Quick Recommendations

| Text Model | Recommendation | Thinking | Temperature | Top P | Top K | Context window | Timeout (s) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `gemma4:e4b` | **Default choice** | On | Blank | Blank | Blank | 32,768 | 600 |
| `gemma4:e2b` | Fast drafts; review every recipe | On | Blank | Blank | Blank | 32,768 | 300 |
| `gemma4:12b` | Works, but less accurate than E4B | **Off** | Blank | Blank | Blank | 32,768 | 300 |
| `gemma4:12b` with thinking | Experimental | On | **1** | **0.95** | **64** | 32,768 | 900 |
| `gemma4:26b-a4b` | Not recommended on 16 GB; try **GPU layers: 24** for partial offload | On | **1** | **0.95** | **64** | 32,768 | 1,200 |
| `gemini-2.5-flash-lite` | Ultra-fast hosted draft extraction | **Off** | Blank | Blank | Blank | 32,768 | 120 |
| `gemini-2.5-flash` | Balanced hosted extraction & refinement | **Off** | Blank | Blank | Blank | 32,768 | 180 |
| `gemini-4-argon` | Deep-reasoning hosted extraction & refinement | **On** | Blank | Blank | Blank | 32,768 | 300 |

*Blank* means leave the field empty so LocalRoast uses its defaults (see [Sampling Settings](#sampling-settings)). For every model, keep **Auto-publish** off and keep-alive at `5m`.

**Connections, models and workflows:** LLM Settings has four tabs. **Connections** saves each machine or provider (endpoint, API key, concurrency and rate limits, CPU-only). **Models** saves named model configurations: pick a connection, choose a discovered model, then set Thinking, Sampling, Context window and Timeout. **Workflows** chooses which saved configuration the Text, Vision and Refinement workflows use, so switching machines or models is a dropdown change. **Profiles** manages the recommended profiles. Concurrency and rate limits apply per connection, so workflows on different machines run at the same time.

**One-click profiles:** the model editor on the **Models** tab has a **Recommended settings** picker with one profile per row of this table. Choose a profile and select **Apply** to fill Thinking, Sampling, Context window and Timeout, then save the configuration. Applying a profile never renames your model; it only fills the model when it is blank, so a derived model (for example one with `num_gpu 24`) can use a profile. The same data is available from `GET /api/llm-settings/model-profiles`.

**Extraction pipeline ("tag, don't rewrite"):** every Text Model uses the same two requests per recipe. The first extracts metadata and the shopping ingredient list. LocalRoast then keeps each method paragraph verbatim as a step, finds every amount and duration in it, and the second request only classifies those spans (which ingredient row an amount measures, or *mixture* / *other*, which rows each step uses, and a timer label). LocalRoast assembles the steps and reconciles the tagged amounts with the ingredient rows; remaining issues route the draft to review. There is no extraction strategy setting and no automatic repair loop. See [Tagged pipeline](#tagged-pipeline-current-code) for the measured comparison with the previous pipeline.

- **Context window:** LocalRoast sends the **Context window** from LLM Settings (default 8,192) with every Ollama request. Use 32,768 for these models. At 16,384, E2B, E4B and 12B with thinking all failed the long multi-day stress recipe because the prompt plus the model's output filled the window. At 32,768 they finished it, and generated just as fast as at 16,384 (they still fit entirely on the 16 GB GPU). On Ollama, each request may generate up to the larger of 16,384 tokens and the context window, so a 32,768 window also lifts the output cap that made `gemma4:26b-a4b` with thinking fail the stress recipe (it now finishes, using 31,530 of the 32,768 tokens). OpenAI-compatible servers keep a 16,384-token cap, because some reject `max_tokens` larger than the remaining context.
- **GPU layers (Ollama only):** on the **Models** tab, edit the model configuration and set **GPU layers** to send `options.num_gpu` on every native Ollama text or vision request, including refinement. Blank omits the option, preserving Ollama/Modelfile placement defaults; `0` uses CPU only; a positive whole number offloads that many layers to the GPU with the remainder in system RAM. Connection-level **Run Ollama on CPU only** always overrides it with `0`. Profiles do not change this hardware-specific value. OpenAI-compatible requests never send it. This is a manual placement override, not automatic maximum-VRAM tuning or an automatic retry.
- **Source sectioning:** pasted text, digital PDFs and web pages without complete recipe data first get one extra request that labels every source line, so extraction and validation see a clean `Ingredients:` / `Instructions:` layout. It follows the **Thinking** toggle and took 7–41 seconds per recipe for E4B with thinking. See [Source sectioning](#source-sectioning).
- **Timeout:** applies to each request, not the whole job, and accepts up to 1,800 seconds.
- **Keep-alive:** the default `5m` keeps the model loaded for the back-to-back requests of a single job and for consecutive queued jobs. Raise it (for example `30m`) only if the queue has long gaps. Models loaded in about 9–16 seconds on the test server.
- **Auto-publish:** keep it off for all of these models. The quality gate already blocks drafts with detectable problems, but zero detected issues does not prove the recipe is correct.
- **These settings are household-wide.** Thinking, Sampling and Context window apply to every LLM request: the Vision Model, ingredient substitutions, kitchen questions and meal-plan suggestions as well as text ingestion.

---

## Sampling Settings

LLM Settings has three optional fields under **Sampling (optional)**: **Temperature**, **Top P** and **Top K**.

- **Blank (recommended unless the table says otherwise):** LocalRoast's defaults.
  - Recipe extraction (sectioning, ingredient, tagging and vision transcription requests) uses temperature 0, which picks the most likely token every time. Runs are still not exactly repeatable on a GPU server (see [Run-to-run variation](#run-to-run-variation)).
  - Substitutions, kitchen questions and meal-plan suggestions use temperature 0.2.
  - Top P and Top K are not sent, so the model's own defaults apply. Every Gemma 4 tag in the Ollama library ships with Top P 0.95 and Top K 64; at temperature 0 they have no effect.
- **Set:** each value is sent with every request, for both Ollama and OpenAI-compatible providers. Clear a field to return to the default. Some hosted OpenAI-compatible APIs reject Top K; leave it blank for those.
- **Why `gemma4:12b` with thinking needs them:** at temperature 0 it repeated the same reasoning until it hit the output limit and failed (2 of 2 recipes). Temperature 1, Top P 0.95 and Top K 64 (the defaults the Gemma 4 models ship with) fix that.
- **A Modelfile cannot replace the Temperature field.** LocalRoast always sends a temperature, which overrides `PARAMETER temperature`. Modelfile Top P and Top K do apply while those fields are blank, so on Ollama Temperature 1 alone gives Gemma 4 its full default sampling. Set all three anyway so the profile also works on servers with different defaults.
- **Side effects of setting them:**
  - Extraction varies even more from run to run: importing the same recipe twice can give quite different drafts. In two `gemma4:12b` runs, one pancake recipe came back as 16 focused steps and then as 3 long paragraphs.
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
- **Score:** *findings* are evaluator assertion failures (lower is better). Examples are wrong totals or splits, amounts left in step text, invalid ingredient links, missing timers and failed extractions; a failed recipe counts as one finding. Findings do not measure how well the method is split into steps, so the results also count steps and steps over 300 characters: long steps combine several actions, which makes divided ingredients and timers harder to follow. Each configuration ran once, and results vary between runs even at temperature 0 (see [Run-to-run variation](#run-to-run-variation)), so treat differences of a few findings as noise.
- **Code version:** the sections up to [Context window on long recipes](#context-window-on-long-recipes) were measured on the branch that added the Sampling settings, which includes per-step `ingredientAmounts`, `alternativeMeasurement`, the configurable context window and the CPU-only option, except where a row says otherwise. [Source sectioning](#source-sectioning), [Repair rounds](#repair-rounds-removed) and [Run-to-run variation](#run-to-run-variation) used the later code that added source sectioning, Repair Rounds, the correction request for every model and the larger Ollama output cap. Earlier numbers from commit `8eb9ac3` are only quoted for comparison.
- **Context window:** the sampling comparison ran at 16,384 tokens. Recipes that failed there were re-run at 32,768; `gemma4:12b` without thinking and `gemma4:26b-a4b` ran at 32,768 throughout. Recipes that finished at 16,384 were not re-run: the window only matters once it fills.

---

## Measured Results

### Tagged pipeline (current code)

The phase that introduced "tag, don't rewrite" extraction compared it with the previous pipeline (staged ingredient and instruction requests that rewrote step text, plus one automatic repair round) on nine ATK cases from `scripts/atk_ingestion_cases.json` (including the `c012-ss286` sourdough starter added in that phase), with `gemma4:e4b`, thinking, blank Sampling and a 32,768-token context:

| Pipeline | Issues | Total time (9 cases) |
| --- | --- | --- |
| Previous (staged + repair) | 10 | ~1,372 s |
| **Tagged (current)** | **3** | **~550 s** |

- The 3 remaining issues are expectation-only, not extraction errors: the chicken piccata's "half of the chicken" cannot be expressed as a 2 + 2 split of its row, and the pancakes' separated eggs (whites and yolks) are modeled differently from the case's expectations.
- Step text is now the verbatim source paragraph, so findings about amounts left in step text, rewritten steps and long steps no longer apply. The sections below were measured with the previous pipeline; use them for the relative model, sampling and context-window comparisons, not as current issue counts.

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
| Beef Wellington | 26B, thinking, Gemma sampling | 21,256 (output capped); 31,530 after the cap was raised | Not run | Failed when output reached the old 16,384-token cap. After the cap was raised: finished, 2 findings |

Generation speed did not change between the two sizes: about 150–160 tokens per second for E2B, 91–100 for E4B and 35–36 for 12B. Partly offloaded, 26B generated 39–42 tokens per second with thinking and 46–49 without. The largest successful output was 15,707 tokens (26B with thinking, shrimp risotto), close to the 16,384-token output cap LocalRoast used at the time. Since the cap now follows the context window on Ollama, 26B finished the Beef Wellington: 18,630 output tokens for extraction and 21,568 for the correction request, which peaked at 31,530 of the 32,768-token window.

### Source sectioning

The evaluator's `--source-style` option renders each recipe in one of three layouts:

- **canonical:** the `Ingredients:` / `- line` / `Instructions:` layout LocalRoast builds from complete recipe data. Extraction starts directly, with no sectioning request.
- **plain:** pasted notes. Title, yield, description, ingredient lines and method paragraphs, with no headings, bullets or step numbers.
- **web:** copied page text. Site navigation and a byline, shouted `INGREDIENTS` / `DIRECTIONS` headings, `•` bullets, numbered steps, a comment prompt at the end, and every paragraph hard-wrapped at 72 characters.

All runs used thinking, blank Sampling, a 32,768-token context and Repair Rounds 1.

| Model | Source | Findings | Steps (over 300 characters) | Mean time per recipe | Sectioning request: mean / longest | Method text put outside the steps |
| --- | --- | --- | --- | --- | --- | --- |
| `gemma4:e4b` | canonical | 19 | 83 (16) | 249 s\* | — | — |
| `gemma4:e4b` | plain | **10** | 95 (8) | 163 s | 13 s / 25 s | none |
| `gemma4:e4b` | web, wrapped lines kept | 17 | 93 (11) | 189 s | 20 s / 41 s | 2 passages |
| `gemma4:e4b` | web, wrapped lines rejoined | 12 | 85 (16) | 155 s | 20 s / 41 s | 1 sentence |
| `gemma4:e2b` | canonical | 35 | 107 (8) | 66 s | — | — |
| `gemma4:e2b` | plain | 35 | 128 (5) | 71 s | 9 s / 16 s | none |
| `gemma4:e2b` | web, wrapped lines rejoined | 36 | 105 (8) | 76 s | 14 s / 29 s | 1 sentence |

\*Another benchmark shared the server during this run, which inflated its time.

- **Every ingredient line was labeled correctly** in all of these runs, by both models. The one method sentence left out of the steps was the bundt cake's closing storage note, which both models labeled as a note instead of a step; the text is kept, just not as a step.
- **Sectioning did not cost accuracy.** E2B scored 35–36 in every layout. E4B scored 10–12 with sectioning and 19 on the canonical layout, but an earlier canonical run scored 10, so that gap is within [run-to-run variation](#run-to-run-variation).
- **Rejoining wrapped lines matters.** With the 72-character wraps kept, the bundt cake scored 11, 4 and 6 findings in three E4B runs; with them rejoined, 4, 2 and 4. Wrapped text splits sentences across lines, so a continuation line that starts with a number (such as "1-inch border") looks like an ingredient line, and amounts or durations can be separated from the rest of their sentence. LocalRoast now rejoins wrapped prose and numbered steps before extraction, and keeps short heading lines (such as "For the glaze") on their own line.
- **Cost:** one extra request per recipe, 9–20 seconds on average and at most 41 seconds.

### Repair rounds (removed)

Earlier versions sent up to **Repair Rounds** correction requests after extraction. Measurements showed a second round never lowered the issue count, and E2B often returned its draft nearly unchanged even when its thinking planned the fixes. The tagged pipeline no longer rewrites steps, so the setting and the automatic loop were removed: remaining reconciliation issues go straight to review, and **Re-extract with AI** is the manual way to run extraction again.

### Repairing a draft during review

The review editor lists validation issues (ingredient-row problems and tagged-step reconciliation issues) and advisory quality warnings, such as vague timers and ingredients mentioned in a step but not linked. These checks can produce false positives and do not prove a recipe is correct. Advisory warnings do not change automatic publication eligibility. Use **Recheck issues** after editing.

**Re-extract with AI** re-runs the tagged extraction with an optional instruction and the current ingredient rows as context, using the configured Text Model. It uses the stored structured source when available (for photo and scanned-PDF jobs, the Vision Model's verbatim transcription), otherwise the normalized or original text. Steps are always regenerated from the source paragraphs, so step-text edits are replaced. Requests use the same sampling, thinking, context and per-request timeout settings described above, so a re-extraction can take several minutes.

Compare the proposed fields, ingredient links, portions, timers and diagnostics with the source. The preview calls out new or changed findings even when the total finding count stays the same or decreases; diagnostics cannot catch every mistake. Applying the preview changes only the editor: **Save Draft** or **Approve & Publish** remains a separate action. Cancel leaves the editor unchanged. If the editor or stored job changes while repair is running or its preview is open, the proposal is not applied; keep your edits and rerun after resolving the conflict. Unsafe mappings of duplicate ingredient metadata or step images are rejected instead of guessed.

### Run-to-run variation

Temperature 0 does not make extraction repeatable on the test server. Two identical E4B requests (same prompt, settings and loaded model) produced the same reasoning for about 500 characters and then diverged, and the drafts and findings diverged with them. Likely causes are floating-point differences in GPU kernels and Ollama's prompt cache; this was not investigated further.

- Three runs of the hard-wrapped bundt cake with identical code and settings scored 11, 4 and 6 findings.
- The eight-case E4B total on the canonical layout was 10 in the run used for the tables above and 19 in a later run. Between those runs the context window went from 16,384 to 32,768 tokens and the output cap was raised, but no request in the later run produced more than 10,407 tokens, so the cap was never reached.
- Treat a single eight-case run as accurate to within about 5–10 findings. Before concluding that a setting or prompt change helped, repeat the run or replay the same requests with only the change applied.
- For households: importing the same recipe again can give a different draft. If a draft has problems, re-running the import is a reasonable first step.

### Changes since `8eb9ac3`

- E4B improved from 13 to 10 findings on all eight cases.
- E2B got worse, from 31 to 43 findings at the same 16,384-token context. Most of the increase is in the shrimp risotto (17 findings: amounts left in step text and rewritten source lines) and the bundt cake (9: split totals for the pan portions).
- 12B with thinking and Gemma sampling had 8 findings on the three hardest cases then and 10 now.

---

## Per-Model Notes

### `gemma4:e4b`

- **Set:** Text Model `gemma4:e4b`, thinking on, Sampling blank, context window 32,768, timeout 600 seconds, keep-alive `5m`, auto-publish off.
- **LocalRoast sets automatically:** source sectioning for sources without an `Ingredients:` / `Instructions:` layout, then the two tagged extraction requests (ingredients, then amount and timer tags). Temperature is 0 while Sampling is blank, and the repetition penalty is 1. The timeout applies to each request, not the whole job.
- **Weaknesses:**
  - divided ingredients that are not split (chicken and oil cooked in batches)
  - wrong split totals for separated eggs
  - amounts left in step text (3 of 10 findings)
  - missing preparation notes and a dropped lime juice row
  - some long steps: 15 of 79 were over 300 characters, the longest 939 (bundt cake). Findings do not count these.
- **Long recipes:** the stress recipe's correction request used 20,410 tokens. It failed at 16,384 and finished at 32,768 with one finding.

### `gemma4:e2b`

- **Set:** the same as E4B, but a 300-second timeout is enough (the longest request measured was 66 seconds).
- **Tradeoff:** about 2–2.5 times faster than E4B, with three to four times as many findings (35–36 in all three [source styles](#source-sectioning) and 44 in a later canonical run, against 9–19 for E4B).
- **Weaknesses:**
  - amounts left in step text (11 of 49 findings)
  - source ingredient lines rewritten instead of copied verbatim
  - wrong split totals (butter and flour for the pan, separated eggs)
  - missing timers and ingredients not linked to any step
- **Use it** when speed matters and you will review every recipe.

### `gemma4:12b`

- **Fits entirely in 16 GB of VRAM** at a 32,768-token context and generated about 35–36 tokens per second. The measurements below predate the tagged pipeline: it extracted each recipe in a single request, and its most common finding was amounts left in step text, which no longer applies now that steps are verbatim.
- **Recommended: thinking off, Sampling blank,** context window 32,768, timeout 300 seconds. 28 findings, 90 seconds per recipe on average (longest request 177 seconds) and repeatable output. It split methods into focused steps (102 steps, 9 over 300 characters), but did not beat E4B on findings.
- **Thinking on needs Temperature 1, Top P 0.95 and Top K 64.** At temperature 0 it repeated the same reasoning until it hit the output limit. With those values it worked:
  - 33 findings in about 4.2 minutes per recipe. It did best on the three hardest cases (10 findings versus 15 without thinking), but worse on the rest.
  - Use a 900-second timeout (one request took 430 seconds) and the 32,768-token context (two recipes failed at 16,384).
  - Output changes from run to run, and the household-wide sampling also applies to helpers and the Vision Model.

### `gemma4:26b-a4b`

- **Memory:** the 17.4 GB mixture-of-experts model does not fit in 16 GB of VRAM. On the test server it failed to load with a GPU out-of-memory error. Setting **GPU layers** to `24` (see [Ollama Server Configuration](#ollama-server-configuration)) keeps 24 layers on the GPU and runs the rest from system RAM; it loaded in 16 seconds. All 26B measurements passed `num_gpu` 24 through the evaluator. CPU-only mode also loads the model, but runs entirely from system RAM and was not benchmarked for extraction.
- **Speed:** only about 4 billion of its parameters are active for each token, so even partly offloaded it generated 39–49 tokens per second, faster than 12B.
- **Previous pipeline:** like 12B, its measurements below used a single request per recipe; amounts left in step text were 10 of its 19 findings with thinking and 9 of 32 without.
- **With thinking (Temperature 1, Top P 0.95, Top K 64):** the most accurate configuration after E4B, with 19 findings and 9 on the three hardest cases (E4B: 6). However:
  - It averaged 5.4 minutes per recipe, twice E4B's time, and produced 8,100–16,400 output tokens per recipe, mostly reasoning.
  - The stress recipe (Beef Wellington) used to fail when its output reached LocalRoast's fixed 16,384-token output cap. Now that the cap follows the context window, it finished with 2 findings, but took 20 minutes. Extraction produced 18,630 output tokens in 530 seconds, and the correction request 21,568 in 695 seconds, at 32–37 tokens per second. The correction peaked at 31,530 of the 32,768-token window, so longer recipes may need a larger window, which leaves less VRAM for model layers.
  - It split the shrimp risotto into 5 steps and the mashed potatoes into 3; the other models produced 12–16 and 6–9. On the stress recipe, 10 of its 17 steps were over 300 characters.
  - Use a 1,200-second timeout (the longest request took 695 seconds). Thinking at temperature 0 was not tested; expect the same looping as 12B.
- **Without thinking (Sampling blank):** 75 seconds per recipe and no failures, but 32 findings, and it mostly copied the source's method paragraphs as steps: 51 steps in total, 23 of them over 300 characters. The pancakes became 3 steps where E2B, E4B and 12B without thinking produced 15–18.
- **Not recommended on a 16 GB GPU:** it needs partial GPU offload, and with thinking it is two to three times slower than E4B, less accurate and writes long steps. Revisit it with more VRAM, or as a [Refine Model](#refine-model-background-second-pass) for drafts that still have issues.

---

## Refine Model (background second pass)

The optional **Refinement** workflow in LLM Settings uses a second, usually larger and slower model configuration — for example `gemma4:12b` with thinking, or a derived `gemma4:26b-a4b`, possibly on another machine — that re-checks drafts which still have problems after the Text Model's extraction. Create the configuration on the **Models** tab (the **Recommended settings** picker fills its thinking, sampling, context window and timeout), then select it for **Refinement** on the **Workflows** tab. Leave Refinement unassigned to turn the feature off.

- **When it runs automatically:** after a text job (web, pasted text or digital PDF) reaches review, LocalRoast queues a refinement if a Refine Model is set, it differs from the Text Model, and the normalized draft still has validation issues or quality warnings. Photo and scanned-PDF jobs are not queued automatically.
- **Manual request:** the review screen offers **Refine with _model_** for any draft ready for review, including photo jobs (their original images are sent to the Refine Model, so it must support images).
- **Never blocks imports or other AI features:** refinement runs only while no imports are queued. A newly queued import, a "Re-extract with AI" preview, or a kitchen-helper request (substitutions, questions, meal plans) pauses the running refinement. It goes back to waiting and starts again from the beginning later. Interrupted refinements also resume after a server restart. Changing LLM Settings can pause it the same way.
- **What it sends:** one repair request using the Refinement configuration's model, thinking, sampling, context window and timeout. It runs the same tagged extraction with the current ingredient rows as context.
- **What counts as an improvement:** the proposal is kept only when it has fewer validation issues, or the same number and fewer quality warnings. Otherwise the review screen reports that the refine model found nothing to improve.
- **Reviewing it:** the review screen shows the status and the before → after count of issues plus warnings. **Compare refined draft** opens the same comparison as **Re-extract with AI**; applying it replaces the editor contents (your food links, collections and tags are kept) and does not save. If the saved draft changed after the refinement started, a warning asks you to compare carefully. **Dismiss** (or **Cancel refinement** while it waits or runs) removes it. Re-running a job's import discards any refinement.
- **Memory:** with `OLLAMA_MAX_LOADED_MODELS=1`, switching between the Text Model and the Refine Model reloads each model. Expect the load time (about 10–60 seconds, longer for partly offloaded models) on top of the refinement itself.

---

## Hosted OpenAI-Compatible Providers (OpenRouter)

LocalRoast's **OpenAI-Compatible (/v1)** provider works with hosted services such as OpenRouter; no code or server changes are needed.

- **Connect:** choose the OpenAI-Compatible provider, set the Base URL to `https://openrouter.ai/api/v1` (LocalRoast adds `/v1` only when it is missing), paste the API key, and select **Test connection**. Every model the service lists (for example `google/gemini-…` IDs) is offered in the Text, Vision and Refine Model fields. Type any words from the name, such as `gemini flash`, to narrow the list.
- **Pick models that support structured outputs.** Every request asks for strict JSON-schema output (`response_format: json_schema`). Models or routes without structured-output support may reject the request or return invalid JSON. OpenRouter's model list can be filtered by this feature.
- **Settings that only apply to Ollama:** Context window, Keep alive, Run Ollama on CPU only and the thinking toggles are not sent. Each request may generate up to 16,384 tokens.
- **Sampling:** Temperature is always sent. Top P and Top K are sent when set; leave Top K blank if the provider rejects it. The built-in profiles were measured on local Gemma models through Ollama, so treat their values only as a starting point.
- **Refine Model:** it uses the same provider, Base URL and API key as the Text Model, so it cannot mix a local Ollama model with a hosted one.
- **Hosted Gemini Profiles:** LocalRoast provides built-in profiles for Google's Gemini models via OpenAI-compatible gateways:
  - `gemini-2.5-flash-lite`: 120s timeout, thinking off. Designed for ultra-fast, economical bulk ingestion.
  - `gemini-2.5-flash`: 180s timeout, thinking off. Balanced speed and extraction fidelity; recommended for primary extraction or refinement.
  - `gemini-4-argon`: 300s timeout, thinking on. High-reasoning model for complex, multi-component recipes and background refinement.
- **Rate Limit & Quota Safeguards:** Under **Refinement & Queue**, the **Enable rate limit and quota safeguards** toggle (default: on) protects both your local pipeline and remote provider quotas:
  - **In-place backoff & jitter:** On transient `HTTP 429` / `Too Many Requests` errors, requests retry in-place up to 3 times, honoring provider `Retry-After` headers and retry delays up to 60 seconds.
  - **Automatic quota exhaustion pause:** If the provider responds with hard daily quota exhaustion (`RESOURCE_EXHAUSTED`, `daily quota exceeded`, or zero remaining quota), LocalRoast detects that retrying immediately is futile, updates the job with an explicit explanatory message, and auto-pauses background ingestion (`EnableIngestion: false`) to avoid endlessly hammering the API.
  - **Canary job promotion:** When checking whether an external provider has recovered, the background worker promotes only a single waiting job back to the queue to verify connectivity before allowing other jobs to process.
  - When disabled, rate limit errors immediately transition jobs to `Failed` without in-place retries or auto-pausing.
- **Concurrency & Rate Pacing:**
  - **Max Concurrent Requests (Slider 1–5, default: 1):** Configures how many background ingestion jobs can execute against the LLM concurrently.
    - *Local Ollama / 16 GB GPUs (e.g. RTX 2000 Ada with Gemma 4 E4B):* Keep at **1** (or at most **2** if using E2B with multiple Ollama parallel slots). Running multiple parallel requests on a single 16 GB GPU with a 32,768 context window can cause context KV cache VRAM spikes or serial queue contention inside Ollama.
    - *Cloud / Hosted Providers (e.g. Gemini Flash Lite, Gemini Flash):* Can safely be raised to **2–5** for much faster throughput when processing batches of recipes.
  - **Requests Per Minute (RPM):** Proactive client-side rate limiter using a 60-second sliding window. For instance, setting this to `15` matches Google Gemini Free Tier's 15 RPM rate limit, smoothly pacing requests so they never trigger `429 Too Many Requests`.
  - **Daily Request Quota (RPD):** Tracks outbound requests across the configured provider quota day. If the count reaches this limit (e.g., `1500` for Gemini Free Tier), the queue auto-pauses with an informative message.
  - **Daily Quota Reset Schedule (Timezone & Time):**
    - **Timezone:** Select your provider's quota rollover timezone. Google AI Studio quotas reset daily at **midnight Pacific Time (`America/Los_Angeles`)**, whereas OpenAI and OpenRouter reset at **midnight UTC (`UTC`)**. Built-in Gemini profiles automatically set this to `America/Los_Angeles`.
    - **Reset Time of Day:** Time in `HH:mm` format when the provider rolls over (default `00:00` / midnight).
    - LocalRoast automatically handles Daylight Saving Time transitions (e.g. PST vs PDT) when calculating the exact UTC timestamp for the next quota rollover.
  - **Auto-Resume Queue:** When enabled (default: on), an ingestion queue that was paused due to daily quota exhaustion will automatically resume at the start of the next quota cycle (e.g., `00:00 PT` / `08:00 UTC` for Google AI Studio), without requiring manual intervention.
- **Privacy and cost:** the source text, photos and drafts are sent to the hosted provider, and usage is billed by that provider. LocalRoast still sends one request at a time.

---

## Ollama Server Configuration

- **One request and one model at a time:** set `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1` on smaller GPUs so ingestion requests never compete for VRAM.
- **Context:** set it in LocalRoast's LLM Settings. The `num_ctx` LocalRoast sends with each request overrides `OLLAMA_CONTEXT_LENGTH` and any Modelfile `num_ctx`.
- **Sampling:** set Temperature in LocalRoast's LLM Settings; LocalRoast always sends one, so a Modelfile `PARAMETER temperature` has no effect. Modelfile `top_p` and `top_k` apply only while those LLM Settings fields are blank (see [Sampling Settings](#sampling-settings)).
- **Partial GPU offload for `gemma4:26b-a4b`:** no server-side derived model is needed. Edit its configuration on LocalRoast's **Models** tab, set **GPU layers** to `24` as a starting point for a 16 GB GPU, and leave the connection's **Run Ollama on CPU only** off. Select that configuration for the desired workflow. The measured evaluator runs passed `options.num_gpu=24`; the setting now sends the same override. Lower it if allocations still fail, or cautiously increase it to use more VRAM. Available VRAM, context length, concurrent requests and backend affect what fits; system RAM must also fit the CPU portion. GPU allocation failures can occur even when automatic fitting is enabled. The A4B active-parameter count does not mean only 4B weights need memory.

  A server-side Modelfile remains an alternative for clients that do not send `num_gpu`:

  ```text
  # Modelfile
  FROM gemma4:26b-a4b
  PARAMETER num_gpu 24
  ```

  ```powershell
  ollama create gemma4-26b-recipes -f Modelfile
  ```

  A nonblank LocalRoast **GPU layers** setting overrides this Modelfile value.
- **Check GPU placement:** run `ollama ps`. `100% GPU` means the model is fully on the GPU; a CPU/GPU split means part of it runs from system RAM and is slower. On the Vulkan test server `ollama ps` reported implausibly small sizes, so a drop in tokens per second after changing the context window was the more reliable sign.

---

## Limitations & Future Work

- These are small, single-run samples from one cookbook format. Re-run the evaluator on your own hardware before changing household defaults, and keep reviewing recipes with separated eggs, prepared mixtures, alternatives and repeated batches.
- Vision models (cookbook photos and scanned PDFs) were not benchmarked.
- The evaluator's `--temperature`, `--top-p`, `--top-k` and `--context-window` flags mirror the LLM Settings fields. To mirror **GPU layers**, add `--ollama-options '{"num_gpu":24}'`. Options are recorded with every result and never change LocalRoast or server defaults:

  ```powershell
  python scripts\evaluate_atk_ingestion.py --base-url http://localhost:11434 --thinking `
    --models gemma4:12b --ids c006-s368 c002-s84 c014-s1134 `
    --temperature 1 --top-p 0.95 --top-k 64 --context-window 32768 `
    --timeout 900 --output "C:\PrivateBenchmarks\atk-12b-sampled"
  ```

- Planned improvements are tracked in the [ingestion improvements plan](plans/ingestion-improvements.md). The ones that affect these settings:
  - Profiles do not set GPU layers; configure this hardware-specific value on each saved model configuration.
  - Find the cause of E2B's regression since `8eb9ac3`.
