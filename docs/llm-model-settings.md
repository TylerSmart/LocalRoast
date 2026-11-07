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
| `gemma4:26b-a4b` | Not recommended on 16 GB; needs a derived model with `num_gpu 24` | On | **1** | **0.95** | **64** | 32,768 | 1,200 |

*Blank* means leave the field empty so LocalRoast uses its defaults (see [Sampling Settings](#sampling-settings)). For every model, keep **Auto-publish** off and keep-alive at `5m`.

- **Context window:** LocalRoast sends the **Context window** from LLM Settings (default 8,192) with every Ollama request. Use 32,768 for these models. At 16,384, E2B, E4B and 12B with thinking all failed the long multi-day stress recipe because the prompt plus the model's output filled the window. At 32,768 they finished it, and generated just as fast as at 16,384 (they still fit entirely on the 16 GB GPU). On Ollama, each request may generate up to the larger of 16,384 tokens and the context window, so a 32,768 window also lifts the output cap that made `gemma4:26b-a4b` with thinking fail the stress recipe (it now finishes, using 31,530 of the 32,768 tokens). OpenAI-compatible servers keep a 16,384-token cap, because some reject `max_tokens` larger than the remaining context.
- **Repair Rounds:** keep the default of 1. After extraction, LocalRoast checks the draft against the source and sends up to this many correction requests, for every Text Model. It keeps the draft with the fewest issues and stops as soon as a round does not improve. See [Repair rounds](#repair-rounds) for what more rounds changed.
- **Source sectioning:** pasted text, digital PDFs and web pages without complete recipe data first get one extra request that labels every source line, so extraction and validation see a clean `Ingredients:` / `Instructions:` layout. It follows the **Thinking** toggle and took 7–41 seconds per recipe for E4B with thinking. See [Source sectioning](#source-sectioning).
- **Timeout:** applies to each request, not the whole job, and accepts up to 1,800 seconds.
- **Keep-alive:** the default `5m` keeps the model loaded for the back-to-back requests of a single job and for consecutive queued jobs. Raise it (for example `30m`) only if the queue has long gaps. Models loaded in about 9–16 seconds on the test server.
- **Auto-publish:** keep it off for all of these models. The quality gate already blocks drafts with detectable problems, but zero detected issues does not prove the recipe is correct.
- **These settings are household-wide.** Thinking, Sampling and Context window apply to every LLM request: the Vision Model, ingredient substitutions, kitchen questions and meal-plan suggestions as well as text ingestion.

---

## Sampling Settings

LLM Settings has three optional fields under **Sampling (optional)**: **Temperature**, **Top P** and **Top K**.

- **Blank (recommended unless the table says otherwise):** LocalRoast's defaults.
  - Recipe extraction (text, staged, sectioning, correction and vision requests) uses temperature 0, which picks the most likely token every time. Runs are still not exactly repeatable on a GPU server (see [Run-to-run variation](#run-to-run-variation)).
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
- **Code version:** the sections up to [Context window on long recipes](#context-window-on-long-recipes) were measured on the branch that added the Sampling settings, which includes per-step `ingredientAmounts`, `alternativeMeasurement`, the configurable context window and the CPU-only option, except where a row says otherwise. [Source sectioning](#source-sectioning), [Repair rounds](#repair-rounds) and [Run-to-run variation](#run-to-run-variation) used the later code that added source sectioning, Repair Rounds, the correction request for every model and the larger Ollama output cap. Earlier numbers from commit `8eb9ac3` are only quoted for comparison.
- **Context window:** the sampling comparison ran at 16,384 tokens. Recipes that failed there were re-run at 32,768; `gemma4:12b` without thinking and `gemma4:26b-a4b` ran at 32,768 throughout. Recipes that finished at 16,384 were not re-run: the window only matters once it fills.

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

### Repair rounds

After extraction, LocalRoast checks the draft against the source. While issues remain, it sends a correction request containing the source, the draft and the list of issues. **Repair Rounds** caps how many correction requests one job may send. The loop stops as soon as a round does not lower the issue count, and a correction that raises it is discarded.

All runs used thinking, blank Sampling and a 32,768-token context. The counts below are LocalRoast's own checks on each correction response, not the evaluator's findings.

| Model | Correction requests | Lowered the issue count | Same count | Raised it (discarded) |
| --- | --- | --- | --- | --- |
| `gemma4:e4b` (5 runs, all three source styles) | 28 | 16 | 3 | 9 |
| `gemma4:e2b` (4 runs, all three source styles) | 27 | 6 | 14 | 7 |

- **A second round never helped.** With Repair Rounds 3 on the canonical layout, E4B sent a second correction for 2 of 8 recipes. Neither lowered the issue count, and they added 47 and 124 seconds. E2B never reached a second round, because none of its first corrections in that run lowered the count.
- The eight-case totals with Repair Rounds 3 were 10 findings for E4B and 42 for E2B, against 19 and 35 with one round. Because the second rounds changed nothing, these differences come from the first drafts (see [Run-to-run variation](#run-to-run-variation)).
- **Keep Repair Rounds at 1.** Each extra round is one more request (about 50–125 seconds with E4B) for every recipe that still has issues.
- **E2B rarely fixes its own drafts.** Half of its corrections came back with the same number of issues. Most of the issues it was asked to fix were missing timers (38), fixed amounts left in step text (29) and dropped or reworded source ingredient lines (26).

The issue list was then rewritten so each issue quotes what it refers to: the full text of a missing source line, the step's fixed amounts, the ingredient's name and the timed sentences. To measure only the wording, the same saved E2B drafts were sent again with the old and the new text.

| Issue text | Requests | Source lines fixed | Timers fixed | Step amounts fixed | Issues kept (from 104) | New issues raised |
| --- | --- | --- | --- | --- | --- | --- |
| Old ("Source ingredient line 6: missing") | 18 | 0 of 24 | 10 of 32 | 0 of 30 | 102 | 36 |
| Quoted (current) | 18 | 20 of 24 | 18 of 32 | 3 of 30 | 98 | 93 |

- **Quoting fixed most dropped and reworded lines,** but the total fell only from 102 to 98. E2B adds the restored rows without linking them to a step, so most new issues were "not linked to any step" and "out of range" indices.
- **Unlinked ingredients now name the steps that mention them,** for example `Ingredient 0 "unsalted butter": measured ingredient is not linked to any step; steps 1 and 3 mention it, so add 0 to the ingredientIndices of each step that uses it.` On 3 saved E2B drafts sent twice each, unlinked rows fell from 20 to 15 (plain message: 19) and the total from 40 to 33 (plain: 37). Most of the gain came from one recipe, so treat it as a small improvement.
- **Tried and dropped:** renumbering step links to follow moved rows (rows had rarely moved, so it never helped), asking for restored rows to be appended at the end, and sending a second round when a correction keeps the same issue count (7 → 7 → 7 and 7 → 7 → 6 on two drafts). The loop still stops when a round does not lower the count.
- **End-to-end check with the quoted and hinted issues** (canonical layout, Repair Rounds 1):
  - **E4B** scored **9** findings in a mean of 155 seconds per recipe, its best eight-case total so far (earlier canonical runs: 10 and 19). Five of its 7 corrections lowered the issue count, including the bundt cake from 5 issues to 0.
  - **E2B** scored 44 (earlier: 35). Only 2 of its 7 corrections lowered the count. Most of the difference is the shrimp risotto (17 findings against 11), whose first draft dropped five consecutive ingredient lines. Its correction kept all 12 issues: the model's thinking planned each fix ("Need to link the 4 cups stock"), but the JSON it returned did not include them. A single run is accurate only to about 5–10 findings, so this does not show that the new text hurt E2B; it does show that E2B often cannot carry out fixes it has identified.

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
- **LocalRoast sets automatically:** source sectioning for sources without an `Ingredients:` / `Instructions:` layout, staged extraction (separate ingredient and instruction requests), then up to **Repair Rounds** correction requests. Temperature is 0 while Sampling is blank, and the repetition penalty is 1. The timeout applies to each request, not the whole job.
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
  - corrections that plan fixes in the thinking but return the draft nearly unchanged (see [Repair rounds](#repair-rounds))
- **Use it** when speed matters and you will review every recipe.

### `gemma4:12b`

- **Fits entirely in 16 GB of VRAM** at a 32,768-token context and generated about 35–36 tokens per second. It extracts each recipe in a single request: staged extraction only runs for `gemma4:e2b` and `gemma4:e4b` tags. The measurements below predate running the correction request for every model, so nothing repaired its mistakes then; its most common finding was amounts left in step text, which the correction request targets.
- **Recommended: thinking off, Sampling blank,** context window 32,768, timeout 300 seconds. 28 findings, 90 seconds per recipe on average (longest request 177 seconds) and repeatable output. It split methods into focused steps (102 steps, 9 over 300 characters), but did not beat E4B on findings.
- **Thinking on needs Temperature 1, Top P 0.95 and Top K 64.** At temperature 0 it repeated the same reasoning until it hit the output limit. With those values it worked:
  - 33 findings in about 4.2 minutes per recipe. It did best on the three hardest cases (10 findings versus 15 without thinking), but worse on the rest.
  - Use a 900-second timeout (one request took 430 seconds) and the 32,768-token context (two recipes failed at 16,384).
  - Output changes from run to run, and the household-wide sampling also applies to helpers and the Vision Model.

### `gemma4:26b-a4b`

- **Memory:** the 17.4 GB mixture-of-experts model does not fit in 16 GB of VRAM. On the test server it failed to load with a GPU out-of-memory error. A derived model with `num_gpu` 24 (see [Ollama Server Configuration](#ollama-server-configuration)) keeps 24 layers on the GPU and runs the rest from system RAM; it loaded in 16 seconds. All 26B measurements passed `num_gpu` 24 through the evaluator. CPU-only mode also loads the model, but runs entirely from system RAM and was not benchmarked for extraction.
- **Speed:** only about 4 billion of its parameters are active for each token, so even partly offloaded it generated 39–49 tokens per second, faster than 12B.
- **Single request:** like 12B it extracts in one request. Its measurements below (apart from the stress-recipe runs) predate the correction request for every model; amounts left in step text were 10 of its 19 findings with thinking and 9 of 32 without.
- **With thinking (Temperature 1, Top P 0.95, Top K 64):** the most accurate configuration after E4B, with 19 findings and 9 on the three hardest cases (E4B: 6). However:
  - It averaged 5.4 minutes per recipe, twice E4B's time, and produced 8,100–16,400 output tokens per recipe, mostly reasoning.
  - The stress recipe (Beef Wellington) used to fail when its output reached LocalRoast's fixed 16,384-token output cap. Now that the cap follows the context window, it finished with 2 findings, but took 20 minutes. Extraction produced 18,630 output tokens in 530 seconds, and the correction request 21,568 in 695 seconds, at 32–37 tokens per second. The correction peaked at 31,530 of the 32,768-token window, so longer recipes may need a larger window, which leaves less VRAM for model layers.
  - It split the shrimp risotto into 5 steps and the mashed potatoes into 3; the other models produced 12–16 and 6–9. On the stress recipe, 10 of its 17 steps were over 300 characters.
  - Use a 1,200-second timeout (the longest request took 695 seconds). Thinking at temperature 0 was not tested; expect the same looping as 12B.
- **Without thinking (Sampling blank):** 75 seconds per recipe and no failures, but 32 findings, and it mostly copied the source's method paragraphs as steps: 51 steps in total, 23 of them over 300 characters. The pancakes became 3 steps where E2B, E4B and 12B without thinking produced 15–18.
- **Not recommended on a 16 GB GPU:** it needs a derived model, and with thinking it is two to three times slower than E4B, less accurate and writes long steps. Revisit it with more VRAM, or as a slower second pass for drafts that still have issues (see the [ingestion improvements plan](plans/ingestion-improvements.md)).

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

- Planned improvements are tracked in the [ingestion improvements plan](plans/ingestion-improvements.md). The ones that affect these settings:
  - Per-model profiles (staged mode, thinking, sampling, repair rounds and GPU layers) instead of tag-name checks and household-wide settings, so helpers and the Vision Model do not inherit a text model's sampling.
  - Show validation issues on the review screen, with a "Fix with AI" action that runs the repair loop again with an optional hint.
  - An optional refine pass with a larger model for drafts that still have issues.
  - Find the cause of E2B's regression since `8eb9ac3`.
