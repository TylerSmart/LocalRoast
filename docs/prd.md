# Product Requirements Document (PRD): LocalRoast

**LocalRoast** is a self-hosted household recipe vault, local-AI multimodal recipe ingestion engine, meal planning calendar, and real-time shopping list application. Capture recipes from URLs, cookbook photos, PDFs, or chat assistants via a local LLM (`Ollama` or OpenAI-compatible server) and an embedded Model Context Protocol (**MCP**) server; plan real-world weeks of meals; shop together with live-synced grocery lists; and cook hands-free in the kitchen.

> [!NOTE]
> **Document Scope:** This PRD defines the functional requirements, domain model, user workflows, and phased roadmap for **LocalRoast**—built on `.NET 10`, `Entity Framework Core 10` (SQL Server), and `Angular 22` (Material 3) for self-hosted Docker / TrueNAS Scale deployment.

---

## 1. Executive Summary & Product Vision

**LocalRoast** solves four everyday household kitchen frustrations:

1. **Recipe Sprawl & Link Rot:** Great recipes get buried across ad-heavy food blogs, phone screenshots, PDF printouts, family text messages, and physical cookbooks—and bookmarked URLs eventually disappear or change.
2. **Manual Data-Entry Fatigue:** Typing 18 ingredients and 9 steps by hand stops people from centralizing their recipes. LocalRoast delegates extraction, normalization, timer detection, and shopping-category classification to a local LLM on your LAN while preserving a Side-by-Side Review Inbox for accuracy.
3. **Rigid Meal Calendars:** Most meal planners assume every single meal is a newly cooked recipe on a fixed night. Real households cook multi-dish dinners, eat leftovers on Wednesday, order takeout on Friday, and often buy groceries for four dinners on Saturday without deciding _which_ night to cook each one until 5:00 PM.
4. **Cluttered Grocery Runs & Messy-Hand Cooking:** Shopping lists shouldn't list `"water"` and `"kosher salt"` every week or get out of sync when two people split up in the grocery store—and phones shouldn't dim or force endless up-and-down scrolling while your hands are covered in dough.

### Core Product Pillars

1. **Local-First Multimodal Ingestion & MCP Native:** Turn Web URLs, raw text, multi-page cookbook photos, and PDFs into structured recipes via `Ollama` (or any OpenAI-compatible `/v1` host) with offline-queue resilience—and expose your cookbook, meal calendar, and shopping lists to external AI clients through an embedded `/api/mcp` server.
2. **Canonical Food Catalog & Culinary Math:** Structured quantities, ranges, units, and section headers backed by a shared `FoodItem` dictionary (with shopping categories, aliases, and pantry-staple flags) enable accurate scaling, US/Metric conversion, and consolidated shopping lists.
3. **Flexible Real-World Meal Planning:** Combine `Breakfast`, `Lunch`, `Dinner`, and `Snack / Prep` calendar slots with multi-dish support, `Leftovers` entries, freeform notes, an **"On-Deck" flexible weekly tray**, and reusable **Week Templates**.
4. **Hands-Free Kitchen & Live Co-Shopping:** Screen wake-lock **Cook Mode** with inline step ingredient callouts and floating multi-timers, historical **Cook Logs**, and **SignalR** real-time co-shopping across household phones.

---

## 2. Target Users, Tenancy & Operating Context

| Persona / Actor                          | Primary Surfaces                                          | Key Workflows                                                                                                                                                |
| :--------------------------------------- | :-------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Household Cooks & Planners**           | **Desktop Browser**, **Tablet**, & **Mobile Phone**       | Ingesting & reviewing recipes, curating collections, planning weekly meals, generating shopping lists, cooking in Cook Mode, and logging cook notes/ratings. |
| **Household Co-Shoppers**                | **Mobile Phone (In-Store)**                               | Checking off grocery items in real time across multiple phones and adding ad-hoc household items on a checklist grouped by shopping category.                |
| **External Friends & Family**            | **Public Browser (No Login)**                             | Viewing, scaling, and printing a single shared recipe via a revocable `/share/<token>` link.                                                                 |
| **External AI Assistants (MCP Clients)** | **Claude Desktop, Antigravity, Open WebUI, Voice Agents** | Searching the household cookbook, pushing or enqueueing new recipes, drafting weekly meal plans, and updating shopping lists over `/api/mcp`.                |

### Tenancy & Operating Assumptions

- **Single Global Household per Instance:** Every authenticated user on the server automatically collaborates on one shared household cookbook, food catalog, ingestion inbox, meal calendar, and set of shopping lists—with zero workspace-switching ceremony. Entity auditing (`IAuditable`) and domain logs preserve per-user attribution (_Added by_, _Cooked by_, _Checked off by_).
- **TrueNAS Scale / Homelab HTTPS Environment:** LocalRoast runs as a unified container (`62781:8080`) behind a local HTTPS reverse proxy (e.g., Nginx Proxy Manager / Caddy / Traefik), unlocking browser `navigator.mediaDevices` (mobile camera cookbook scans) and `navigator.wakeLock` (screen stay-awake in Cook Mode).
- **Decoupled LAN LLM Host:** The local LLM machine (e.g., a separate GPU workstation or server running `Ollama`, `LM Studio`, `llama.cpp`, or `vLLM`) may occasionally be busy, loading weights into VRAM, or powered off. LocalRoast's core web UI, cookbook, meal planner, and shopping lists remain 100% instant and deterministic when the LLM host is offline, while ingestion jobs queue safely in SQL Server.
- **Persistent Media Volume:** Downloaded recipe cover photos, step photos, cook log photos, and uploaded source PDFs/scans are stored on a mounted filesystem volume (`/data/uploads`, mapped to `/mnt/tank/localroast/uploads` on TrueNAS Scale) with optimized WebP thumbnails rather than bloating SQL Server `VARBINARY` columns.

> [!IMPORTANT]
> **Explicit Deferred Scope (Nutritional Information):** Calorie and macronutrient tracking are intentionally **out of scope** for initial milestones. However, modeling ingredients with normalized `quantity` + `unit` fields linked to a canonical `FoodItem` entity ensures nutritional profiles (`caloriesPer100g`, `unitWeightGrams`, USDA FDC IDs) can be layered onto `FoodItem` in the future without altering recipe schemas.

---

## 3. Domain Model & Core Entities

```mermaid
erDiagram
    USER ||--o{ PERSONAL_ACCESS_TOKEN : "mints for MCP"
    USER ||--o{ COOK_LOG : "records"
    RECIPE ||--o{ RECIPE_INGREDIENT : "contains"
    RECIPE ||--o{ RECIPE_STEP : "orders"
    RECIPE ||--o{ RECIPE_INGREDIENT : "referenced as Sub-Recipe"
    RECIPE ||--o{ COOK_LOG : "tracks history & tweaks"
    RECIPE ||--o{ SHARE_LINK : "exposes read-only token"
    RECIPE }o--o{ COLLECTION : "organized into"
    RECIPE }o--o{ TAG : "classified by"
    FOOD_ITEM ||--o{ FOOD_ITEM_ALIAS : "matches synonyms"
    FOOD_ITEM ||--o{ RECIPE_INGREDIENT : "normalizes"
    INGESTION_JOB ||--o| RECIPE : "produces draft / recipe"
    MEAL_PLAN_ENTRY }o--|| RECIPE : "schedules (optional)"
    MEAL_PLAN_TEMPLATE ||--o{ MEAL_PLAN_TEMPLATE_ITEM : "stamps"
    SHOPPING_LIST ||--o{ SHOPPING_LIST_ITEM : "contains"
    FOOD_ITEM ||--o{ SHOPPING_LIST_ITEM : "links (optional)"
```

---

## 4. Functional Requirements & Business Logic

### 4.1 Recipe Domain, Canonical Food Catalog & Culinary Math

#### 4.1.1 Recipe Metadata & Media Preservation

- **Core Fields:** `title`, `slug`, `description` (short summary or family story), `status` (`DraftReview` vs `Published` vs `Archived`), `baseServings` (numeric, e.g., `4`), `yieldDisplay` (optional text override, e.g., `"12 muffins"` or `"2 9-inch loaves"`), `prepTimeMinutes`, `cookTimeMinutes`, `totalTimeMinutes`, and `difficulty` (`Easy`, `Medium`, `Project`).
- **Source Provenance:** `sourceUrl`, `sourceName` / `sourceAuthor`, `sourceBookTitle`, `sourceBookPage`, plus optional retention of original uploaded scan/PDF attachments.
- **Anti-Link-Rot Media Storage:** Whenever a recipe is ingested from a URL or uploaded with images, LocalRoast downloads the image bytes locally into `/data/uploads`, strips EXIF orientation issues, and generates responsive `WebP` cover and thumbnail variants.
- **Organization Taxonomy:**
  - **Collections (Cookbooks):** Many-to-many user-curated folders/cookbooks (e.g., _Weeknight Staples_, _Thanksgiving_, _Sourdough & Baking_, _Summer Grilling_).
  - **Typed Tags:** Categorized tags across `Course` (_Main_, _Side_, _Breakfast_, _Dessert_, _Sauce/Component_), `Cuisine` (_Mexican_, _Italian_, _Thai_, etc.), `Dietary` (_Vegetarian_, _Gluten-Free_, _Dairy-Free_), `Equipment` (_Instant Pot_, _Air Fryer_, _Sheet Pan_, _Grill_, _Dutch Oven_, _One-Pot_), and `Custom`.

#### 4.1.2 Structured Ingredients, Sections & Sub-Recipes

- Ingredients within a recipe can be flat or grouped under named **Ingredient Sections** (e.g., `"For the Chicken Marinade"`, `"For the Crispy Shallots"`).
- Each `RecipeIngredient` stores:
  - `quantity` (`decimal?`, e.g., `1.5`) and `maxQuantity` (`decimal?`, e.g., `2.0` for `"1 1/2 to 2 tbsp"`).
  - `unit` (normalized unit code from a culinary unit registry: `tsp`, `tbsp`, `fl_oz`, `cup`, `pt`, `qt`, `gal`, `ml`, `l`, `g`, `kg`, `oz`, `lb`, `pinch`, `dash`, `clove`, `can`, `bunch`, `slice`, `whole`, or `custom`).
  - `foodItemId` (foreign key to the canonical `FoodItem` catalog) + `displayName` (how the ingredient reads in this recipe, e.g., `"extra-virgin olive oil"`).
  - `preparationNote` (e.g., `"finely diced"`, `"room temperature, divided"`).
  - `isOptional` (`bool`) and `originalRawText` (preserved verbatim from import/input for fallback transparency).
- **Sub-Recipe Component Linking:**
  - A `RecipeIngredient` row can optionally link to another saved `Recipe` (`linkedRecipeId`, e.g., _Fish Tacos_ linking to `1x` batch of saved _Pickled Red Onions_ and `0.5x` batch of _Chipotle Crema_).
  - **Cycle Prevention:** Saving a sub-recipe link validates the dependency graph (DAG) and rejects circular references.
  - Viewing the parent recipe lets the cook expand sub-recipe ingredients/steps inline, and adding the parent recipe to a Meal Plan or Shopping List includes component ingredients automatically (unless unchecked).
  - Ingestion recognizes recipe components, links an existing recipe when its title matches, and stores unmatched references separately from the food catalog. Unresolved components remain visible in recipe and cook views and as named shopping-list entries; users can link them later from the recipe editor.

#### 4.1.3 Canonical `FoodItem` Catalog & Pantry Staples

- Every structured ingredient links to (or creates on the fly) a canonical **`FoodItem`** record:
  - `name` (singular canonical display name, e.g., `"Yellow Onion"`, `"Kosher Salt"`, `"Garlic"`).
  - `aliases` (normalized synonym list, e.g., `"scallion"` $\leftrightarrow$ `"green onion"`, `"cilantro"` $\leftrightarrow$ `"fresh coriander"`).
  - `category` (fixed `FoodCategory` set in store-walk order: `Produce`, `Meat & Seafood`, `Dairy & Eggs`, `Refrigerated`, `Bakery & Bread`, `Pantry & Dry Goods`, `Canned & Jarred`, `Condiments & Sauces`, `Spices & Seasonings`, `Baking`, `Snacks`, `Beverages`, `Frozen`, `Other`). Suggested by the LLM during ingestion, falling back to a deterministic keyword classifier; existing items without a category are backfilled at startup.
  - `isPantryStaple` (`bool`, seeded with common staples like _Water_, _Kosher Salt_, _Black Pepper_, _Olive Oil_, _Vegetable Oil_, _All-Purpose Flour_, _Granulated Sugar_, and editable anytime by household users).
- **Catalog Hygiene:** Household users can edit any `FoodItem` (changing its shopping category or toggling `isPantryStaple`) or **Merge Duplicate FoodItems** (re-pointing all `RecipeIngredient` and `ShoppingListItem` rows in a single transaction).

#### 4.1.4 Ordered Steps, Inline Ingredient References & Timers

- Instructions are stored as ordered `RecipeStep` records (optionally grouped under **Step Sections** like `"Make the Roux"` / `"Assemble & Bake"`), supporting optional per-step reference photos.
- **Step-to-Ingredient Callouts:** Steps can reference the specific `RecipeIngredient` rows used in that step (mapped automatically during LLM ingestion or toggled in the recipe editor) so Cook Mode renders exact scaled amounts inline inside the step.
- **Explicit Step Portions:** Each step reference can carry its own amount, range, unit, and custom unit name, editable in review and the recipe editor. Callouts and cooking narration scale that portion, not the whole-recipe quantity. Unmeasured references show only the ingredient name. New extraction instructions keep assigned quantities in callouts rather than repeating fixed quantities in prose; existing instructions remain unchanged.
- **Flat Steps:** Logical sub-steps are separate ordered steps, optionally grouped by section. Redundant source prefixes such as `4A.` and `4B:` are removed; nested step hierarchies are intentionally not supported.
- **Structured Step Timers:** Steps store zero or more `StepTimer` definitions (`durationSeconds`, `label`, e.g., `[900, "Simmer sauce"]`), auto-detected from durations in step text (`"simmer for 15 minutes"`, `"bake 25–30 min"`) or added manually.

#### 4.1.5 Dynamic Scaling & Culinary Unit Formatting

- Any recipe view can be scaled dynamically by quick multipliers (`0.5x`, `1x`, `1.5x`, `2x`, `3x`) or an arbitrary **Target Servings** stepper.
- **Culinary Fraction Formatting:** Scaled US/customary volumes and counts render in natural kitchen fractions (`1/8`, `1/4`, `1/3`, `1/2`, `2/3`, `3/4`, `1 1/2`) rather than awkward decimals (`0.33333 cup`), while metric units (`g`, `kg`, `ml`, `l`) render clean rounded decimals/integers.
- **Unit System Switcher (`Original` | `US Customary` | `Metric`):** Converts volume-to-volume and weight-to-weight units seamlessly (`oz`/`lb` $\leftrightarrow$ `g`/`kg`, `tsp`/`tbsp`/`cup` $\leftrightarrow$ `ml`/`l`) while leaving count-based units (`3 cloves garlic`, `1 bunch cilantro`) intact.
- **Ingredient-Specific Equivalents:** Preserve one source-provided or manually entered alternative measurement (for example, `5 cups` flour equivalent to `25 oz`) and a saved per-ingredient preference. Review/editor controls change the recipe default; reading, cooking, and public-share switches are temporary. Scaled step portions use the authored ratio when switching between equivalents. No universal food density is inferred: volume-only recipes require a manually supplied equivalent before weight can be selected.
- **Shopping Totals:** Shopping and ingredient substitutions use preferred whole-recipe measurements; step portions are never added to recipe totals or counted again.

---

### 4.2 Multimodal Recipe Ingestion Pipeline & Local LLM Engine

```mermaid
flowchart LR
    subgraph Inputs["4 Ingestion Channels"]
        U["Web Recipe URL"]
        T["Raw Text / Markdown"]
        I["Images / Multi-Page Camera Scan"]
        P["PDF Document"]
    end
    subgraph Queue["Persistent SQL Queue"]
        J["IngestionJob (Queued / WaitingForHost / Processing)"]
    end
    subgraph LLM["LAN LLM Host (Ollama / OpenAI v1)"]
        TX["Text Model (URLs, Text, Digital PDFs)"]
        VL["Vision Model (Photos, Scanned PDFs)"]
    end
    subgraph Review["Review & Cookbook"]
        R["Side-by-Side Review Inbox"]
        C["Published Household Cookbook"]
    end
    Inputs --> J
    J --> TX
    J --> VL
    TX --> R
    VL --> R
    R -->|"Approve & Save"| C
    TX -.->|"Optional Auto-Publish"| C
```

#### 4.2.1 Four Core Ingestion Channels

1. **Web Recipe URLs (Hybrid Scraper + LLM Normalizer):**
   - Fetches page HTML with polite browser headers, extracts embedded `schema.org/Recipe` JSON-LD / Microdata when present alongside cleaned readable DOM text, strips life-story blog clutter and ads, downloads the primary recipe photo locally, and feeds the distilled payload to the configured **Text LLM** to normalize messy ingredient strings, infer missing prep/cook times, extract step timers, link step ingredients, suggest tags, and map canonical `FoodItem`s.
2. **Raw Text & Markdown Paste:**
   - Accepts freeform pasted recipes from family texts, emails, notes apps, or AI chat transcripts and runs structured extraction via the **Text LLM**.
3. **Images & Multi-Page Camera Scans (`PNG`, `JPG`, `WEBP`, `HEIC`):**
   - Users can upload 1–6 images per job—or snap consecutive pages of a physical cookbook / front-and-back of a handwritten family recipe card directly from their phone camera—processed together as a single recipe by the configured **Vision LLM**.
4. **PDF Documents:**
   - Inspects uploaded PDFs page-by-page: extracts embedded digital text directly for fast **Text LLM** parsing when text density is sufficient, and automatically rasterizes scanned/image-only pages into images for **Vision LLM** extraction.

#### 4.2.2 Persistent Async Queue & Offline LLM Host Resilience

- Submitting any ingestion input immediately creates an `IngestionJob` in SQL Server and returns control to the user in `< 200ms` (allowing rapid batch submission of 5–10 URLs or cookbook snaps in a row).
- **Job Lifecycle States:** `Queued` $\rightarrow$ `Preprocessing` $\rightarrow$ `Extracting` $\rightarrow$ `ReadyForReview` (or `Published` if auto-publish is enabled), plus `WaitingForLlmHost` and `Failed`.
- **Asleep / Offline GPU Host Handling:** If the configured Ollama / OpenAI-compatible machine is unreachable or times out, the background worker transitions the job to `WaitingForLlmHost` (retaining all uploaded files and scraped HTML/JSON-LD safely on disk/DB), periodically probes host health with backoff, and exposes a 1-click **"Retry All Waiting Jobs"** action once the GPU machine wakes up.

#### 4.2.3 Side-by-Side Review Inbox

- Extracted jobs land in an **Ingestion Review Inbox** badged in the main navigation:
  - **Left Pane (Original Source):** Interactive image zoom/pan carousel for cookbook photos, embedded PDF viewer, raw pasted text, or source URL metadata + extracted raw lines.
  - **Right Pane (Structured Draft):** Full editable recipe form pre-populated with parsed sections, ingredients, canonical `FoodItem` bindings (highlighting _New FoodItems_ with their AI-suggested shopping category, editable before publish), steps, timers, and suggested tags.
  - **Optional Auto-Publish Toggle:** Household settings include an opt-in toggle to auto-publish high-confidence Web URL / Text extractions directly to the cookbook while still keeping vision/scan jobs in the Review Inbox.
  - **Measurement Review:** Source quantities take precedence over model guesses. Invalid portions, unmatched source excerpts, and unconfirmed equivalent measurements produce visible review warnings and prevent automatic publishing, including when the individual job requests auto-publish.
  - **Extraction Quality Gate:** A model's confidence is not sufficient to publish. Detectable source-total/provenance errors, preparation or alternatives embedded in food names, missing timed phases, invalid or missing ingredient links, unrepresented divided additions, and measured intermediate mixtures absent from the ingredient list route to review (including explicitly requested auto-publish jobs), cap confidence below auto-publish thresholds, and explain the issues in the job status. Structural mixture warnings are review-only, not another LLM correction loop. These checks are conservative heuristics, not proof of semantic correctness; manual review remains necessary for complex recipes.

#### 4.2.4 Provider-Agnostic Local LLM Configuration & 1-Click Helpers

- **Supported Backends:** Native **Ollama API** (`/api/chat`, `/api/tags`, structured JSON `format` schema, configurable `keep_alive`) and **OpenAI-Compatible `/v1` API** (`LM Studio`, `llama.cpp`, `vLLM`, `LiteLLM`, `OpenRouter`, etc.).
- **Source Sectioning:** Text sources without the canonical `Ingredients:` / `- line` / `Instructions:` layout (pasted text, digital PDF text, and web pages without complete recipe JSON-LD) first get a line-labeling request. The text model returns exactly one label per numbered line (title, detail, description, note, ingredient group, ingredient, wrapped ingredient continuation, instruction or skip), constrained by a JSON schema with one required property per line; the request follows the household **Thinking** setting and is skipped for sources over 600 lines. Code then corrects common small-model labeling mistakes using only line shape (section headings, breadcrumb titles, method text above the ingredient list, quantity lines before step 1, hard-wrapped continuations, amount-only lines and all-caps group headings) and rebuilds a canonical copy from the original lines, so nothing is reworded or invented. Hard-wrapped ingredients, sentences and numbered steps are rejoined onto one line, so an amount or duration is never split across lines; short method headings such as "For the glaze" stay on their own line. The copy is stored as `StructuredSourceText` and used for extraction and source-line validation, while the review screen keeps the original source. If sectioning fails (including a timeout or an error response) or finds no ingredients or instructions, extraction falls back to the unstructured text.
- **Small Gemma Text Extraction:** Model tags beginning `gemma4:e2b` or `gemma4:e4b` use serial ingredient and instruction passes. The instruction schema bounds indices against the finalized ingredient array. Extraction uses temperature 0 (unless the household sets a sampling override) and repetition penalty 1 while respecting the configured context window, CPU-only option, thinking toggle, keep-alive and per-request timeout. A 32,768-token context is recommended for these tags so long recipes fit; the evaluator defaults to 16,384 unless `--context-window` is given. Other models retain single-pass extraction. Vision shares the nullable, constrained-unit schema and extraction rules but remains single-pass. Measured per-model recommendations are maintained in [Recommended Local LLM Model Settings](llm-model-settings.md).
- **Validation-Guided Repair:** Every text extraction, staged or single-pass and for any model, is checked against the source and sent back for up to **Repair Rounds** correction requests (household setting, 0–5, default 1). Each round lists the remaining issues; the loop keeps the draft with the fewest issues, stops when a round does not improve, and keeps the best draft when a correction is unusable, empties the ingredients or steps, or the host fails. Remaining issues still route the draft to review.
- **Output Limits:** Native Ollama text and vision requests allow up to the larger of 16,384 tokens and the configured context window (`num_predict`), so thinking models are not cut off on long recipes. OpenAI-compatible requests keep `max_tokens` at 16,384 because those servers can reject values larger than the remaining context.
- **Structured Amounts:** Unknown quantities/units are actual JSON nulls; known units use canonical codes, with a separate custom-unit name for other units. Explicit per-step portions use `ingredientAmounts`; separate portion rows remain supported but must conserve source totals. Instruction prose retains process measurements, not duplicated ingredient amounts. If a prepared-mixture portion has no corresponding ingredient row, retain its source amount for manual review rather than silently lose it; such drafts are not multiplier-safe. Source-provided equivalents use `alternativeMeasurement`, never a whole-line weight copied to every split portion. Other parenthetical package weights remain notes. Explicit small measurements are not discarded as hallucinations.
- **In-App LLM Settings UI:** Seeded from optional Docker environment variables (`Llm__Provider`, `Llm__BaseUrl`, `Llm__ApiKey`, `Llm__TextModel`, `Llm__VisionModel`) and editable in-app:
  - Live **"Test Connection & Refresh Models"** button querying available models on the host.
  - Independent **Text Model** and **Vision Model** dropdown selectors (allowing a fast 7B–14B text model for URLs/text and a dedicated vision model like `qwen2.5vl` or `gemma3` for cookbook photos).
  - **CPU-Only Ollama:** The persisted "Run Ollama on CPU only" option sends native text/vision requests with `options.num_gpu=0`; disabled requests leave GPU selection to Ollama. OpenAI-compatible requests are unaffected. CPU inference can be substantially slower.
  - **Sampling (optional):** Household **Temperature** (0–2), **Top P** (0–1) and **Top K** (whole number, 1–1000) apply to every text, vision and kitchen-helper request for both providers. Blank values keep LocalRoast's defaults: temperature 0 for recipe extraction and 0.2 for kitchen helpers, with Top P and Top K omitted so the model's own defaults apply. Some hosted OpenAI-compatible APIs reject `top_k`; leave it blank for them. Recommended values per model, such as 1 / 0.95 / 64 for `gemma4:12b` with thinking, are in [Recommended Local LLM Model Settings](llm-model-settings.md).
  - **Repair Rounds and Timeout:** **Repair Rounds** (whole number, 0–5, default 1; 0 disables repair) sets the maximum number of validation-guided correction requests per text recipe. The per-request **Timeout** accepts 5–1,800 seconds so slow thinking models and long recipes can finish.
  - **Gemma Memory Diagnostics:** A local Gemma 4 26B A4B structured-request check failed during Vulkan buffer allocation with default GPU placement and succeeded with CPU-only placement. The accompanying `Gemma4Assistant requires ctx_other to be set` message is a memory-fitting warning, not a missing LocalRoast configuration field. Allocation failures surface actionable errors instead of waiting indefinitely for an "offline" host; use CPU-only mode, a smaller model/context, or free GPU memory. This verifies model loading and structured output, not full-recipe extraction quality or speed.
- **Targeted 1-Click AI Kitchen Helpers (No Chat Bloat):**
  - LocalRoast uses focused helpers rather than a persistent chatbot drawer or saved chat history.
  - When the LLM host is online, LocalRoast enables three focused actions that gracefully hide/disable when the LLM host is offline:
    1. **Suggest Ingredient Substitution:** Tap an ingredient in a recipe or Cook Mode (e.g., _"Out of buttermilk"_) to get 2–3 culinary substitution ratios tailored to that specific dish.
    2. **Draft Weekly Meal Plan:** Provide quick constraints on the calendar (e.g., _"4 weeknight dinners under 35 minutes, prioritize chicken thighs and recipes we haven't made recently"_) to propose candidate meals strictly from your saved household cookbook.
       - The calendar defaults to **Next 7 Days** (today through six days ahead), with canonical Sunday-based Week and Month views still available.
       - AI drafts start no earlier than the user's local today, even when viewing an earlier week. Scheduled **Times to cook** counts cooking occasions, not leftover meals; the selected slot's remaining unplanned dates are covered by leftovers. **Servings per meal** scales cooking batches to include those portions; leftovers do not add ingredients to shopping lists. On-Deck drafts remain unscheduled recipe suggestions.
       - Each suggestion can be retried independently, excluding recipes already suggested while preserving the other choices and the replaced group's dates, portions, and leftovers. Applying a selected group saves its cooking entry and linked leftovers together.
    3. **Ask Kitchen AI:** Ask a cooking question from recipe detail or Cook Mode, or request an explanation of a specific ingredient, preparation note, or step. Each ephemeral question uses the configured text model and only the relevant recipe context; failures can be retried.

---

### 4.3 Embedded MCP Server (`/api/mcp`)

- **Transport & Hosting:** Mounted directly inside the ASP.NET Core 10 API at `/api/mcp` using the official **Model Context Protocol Streamable HTTP** transport—no sidecar container required.
- **Authentication (Personal Access Tokens):**
  - Household users can generate named, revocable **Personal Access Tokens** (`lr_pat_<random>`) in their Account Settings dialog.
  - Only the SHA-256 token hash and prefix are stored in SQL Server, alongside `createdAt`, `expiresAt` (optional), and `lastUsedAt`. Any write action performed over MCP attributes `CreatedBy`/`ModifiedBy` to the token's owning user.
  - **OAuth sign-in (not yet supported):** The MCP authorization spec requires the server to publish OAuth 2.0 Protected Resource Metadata (RFC 9728) pointing at an OAuth 2.1 authorization server that uses PKCE, and to accept only tokens issued for `/api/mcp` (RFC 8707 resource indicators; no token passthrough). Client registration can use pre-registration, Client ID Metadata Documents (recommended), or Dynamic Client Registration (optional). Google cannot fill the authorization-server role directly because its tokens are not issued for LocalRoast's resource, and accepting them would mean trusting tokens minted for another audience. "Sign in with Google" for MCP therefore requires LocalRoast to act as its own authorization server that delegates the login step to the configured OIDC provider. PATs remain the supported MCP credential until then.
- **MCP Tool Surface:**
  1. **Cookbook & Discovery Tools:**
     - `search_recipes`: Keyword, tag, collection, max total time, rating, and forgotten-favorite filtering.
     - `get_recipe`: Full structured recipe with ingredients, sections, steps, timers, and cook history notes.
     - `match_recipes_by_ingredients`: Rank saved recipes using a list of on-hand ingredient names.
     - `list_collections_and_tags`: Inspect existing household collections, cuisines, and equipment tags.
  2. **Ingestion & Authoring Tools:**
     - `enqueue_recipe_ingestion`: Submit a Web URL or raw text block into the async Ollama ingestion queue.
     - `save_structured_recipe`: Directly create or update a complete structured recipe from an external AI conversation (with a `publishImmediately` flag defaulting to `DraftReview` or `Published` per caller choice).
  3. **Meal Planning Tools:**
     - `get_meal_plan`: Read scheduled meals and the "On-Deck" queue for a given date range.
     - `plan_meals`: Schedule recipes, leftovers, or freeform notes onto calendar slots or the weekly "On-Deck" tray.
     - `remove_meal_plan_entry`: Clear or reschedule planned entries.
  4. **Shopping List Tools:**
     - `get_shopping_lists`: View active shopping lists alongside unchecked, checked, and excluded pantry-staple items.
     - `generate_shopping_list_from_meal_plan`: Build/merge consolidated meal-plan ingredients into a target shopping list.
     - `upsert_shopping_list_items` / `set_shopping_list_item_checked`: Add items (e.g., _"Add paper towels and milk to Costco list"_) or mark items checked.

---

### 4.4 Meal Planning Calendar & "On-Deck" Queue

#### 4.4.1 Multi-Slot Weekly & Monthly Calendar

- Supports both a rich **Weekly Board View** (primary planning workspace) and a **Monthly Overview**.
- Each date supports four ordered **Meal Slots**: `Breakfast`, `Lunch`, `Dinner`, and `Snack / Prep`.
- **Multi-Dish Slots:** Any slot can hold multiple entries (e.g., Sunday `Dinner` containing _Herb Roast Chicken_ + _Garlic Mashed Potatoes_ + _Roasted Broccolini_).

#### 4.4.2 Real-World Entry Types

1. **Scheduled Recipe:** Links a saved `Recipe` with a customizable `targetServings` override (defaulting to the recipe's `baseServings`—e.g., scaling a 4-serving chili recipe up to `8 servings` on Sunday for meal prep).
2. **Leftovers Entry:** Can either link back to a prior scheduled meal (e.g., _"Leftovers: Sunday Chili"_) or stand alone. **Shopping Math Invariant:** Entries marked `isLeftovers = true` display clearly on the calendar but contribute **zero** additional ingredients when generating shopping lists.
3. **Freeform / Ad-Hoc Note:** Quick text entry without a linked recipe (e.g., _"Date Night / Eating Out"_, _"Order Thai"_, *"Frozen Dumplings"`).

#### 4.4.3 The "On-Deck" Flexible Weekly Tray

- Alongside dated Monday–Sunday columns, each week includes an **"On-Deck This Week"** tray.
- Households can drop 3–5 recipes into **On-Deck** on Saturday, include **On-Deck** in their weekly grocery list generation, and drag-and-drop meals from **On-Deck** onto specific nights during the week (or tap **"Cook Tonight"** right from the tray).

#### 4.4.4 Reusable Week Templates

- Users can save any week's arrangement (or craft one from scratch) as a named **Meal Plan Template** (e.g., _"Standard Busy Week"_, _"Hosting Weekend"_) and stamp/merge it onto any target week on the calendar.

---

### 4.5 Smart Shopping Lists, Pantry Staples & Real-Time Co-Shopping

#### 4.5.1 Multiple Named Lists Grouped by Category

- Create unlimited named **Shopping Lists** (e.g., _Weekly Groceries_, _Costco_, _Trader Joe's_, _Thanksgiving Prep_).
- Active items are grouped under **shopping category headings** (from each linked `FoodItem`'s category, or a keyword guess for free-text items) in a fixed store-walk order, so a grocery run moves through the store section by section. There is intentionally no per-store layout configuration—categories are zero-maintenance.
- A **Grouped / Flat list** toggle switches to a plain checklist; the choice is remembered per device.

#### 4.5.2 Smart Consolidation, Unit Math & Provenance Badges

- Generating shopping items from a **Meal Plan Date Range** (with optional toggle to include **On-Deck** meals) or a single recipe:
  1. Multiplies each recipe's ingredients (and active linked Sub-Recipes) by `targetServings / baseServings`, ignoring `Leftovers` and freeform meal notes.
  2. Consolidates rows sharing the same `foodItemId`:
     - **Compatible Units:** Automatically sums volumes with volumes (`2 tbsp` + `1/4 cup` = `6 tbsp` or `3/8 cup` Olive Oil) and weights with weights (`250g` + `500g` = `750g`).
     - **Incompatible Dimension Families:** Keeps distinct measurement dimensions clearly visible side-by-side (`Yellow Onion: 3 whole + 1 cup diced`) rather than guessing unreliable density conversions unless explicitly defined.
  3. **Recipe Provenance Badges:** Every consolidated shopping item retains source references rendered as subtle chips (_"Tue: Chicken Tacos, Thu: Enchiladas"_) so shoppers know _why_ an item is on the list and how critical exact ripeness/cut is.
  4. **Idempotent Re-Generation / Sync Warning:** Re-running meal-plan generation for overlapping dates detects already-imported `MealPlanEntry` slices so ingredients are never accidentally doubled.

#### 4.5.3 Lightweight Pantry Staples Tray

- During shopping list generation, any ingredient whose `FoodItem` has `isPantryStaple = true` (_Water_, _Kosher Salt_, _Black Pepper_, _Olive Oil_, etc.) is automatically routed into a collapsed **"Verify Pantry Staples (Excluded)"** drawer at the bottom of the generation preview / list rather than cluttering active shopping items.
- One tap promotes any staple (e.g., _Olive Oil_) onto the active shopping list when the household happens to be running low.

#### 4.5.4 Real-Time Multi-Phone Sync (`SignalR`)

- Shopping lists connect to an authenticated **SignalR Hub** (`/api/hubs/shopping`) with automatic reconnection on flaky grocery-store cellular/Wi-Fi transitions.
- Checking off an item, adjusting a quantity, or adding an ad-hoc item (`"Paper Towels"`) applies optimistically in `< 50ms` on the local phone and broadcasts immediately to any other household member viewing the list.
- Checked items animate cleanly into a collapsible **"Checked Off (N)"** bottom section with a **"Clear Checked Items"** action when the trip is done.

---

### 4.6 Hands-Free "Cook Mode" & Household Cook Log

#### 4.6.1 Dedicated Cook Mode View

- Launched via a prominent **"Start Cooking"** action on any Recipe or Today's Meal Plan card:
  - **Screen Wake Lock:** Acquires `navigator.wakeLock.request('screen')` (and re-acquires on tab visibility restore) with a visible `"Screen Awake"` indicator so phones and tablets never lock mid-recipe.
  - **Distraction-Free Large Typography:** High-contrast step-by-step cards with active-step focus, step completion checkboxes, and big thumb/knuckle-friendly `Previous` / `Next` targets (plus keyboard arrow navigation).
  - **Inline Scaled Step Ingredients:** Each step card displays mini chips/callouts for the exact scaled quantities of ingredients used in that step (e.g., `[ 1 1/2 tbsp Soy Sauce ]` `[ 2 cloves Garlic, minced ]`), eliminating scrolling back to the top ingredient list.
  - **Floating Multi-Timer Tray:** Tapping any inline duration badge (`15 min`) starts a named countdown timer pinned in a sticky bottom/top bar. Supports running multiple timers simultaneously, `+1 min` quick bumps, pause/reset, and chime + browser notification alerts.
  - **Live Scaling & Unit Toggle:** Adjust servings or flip `Original / US / Metric` live inside Cook Mode.

#### 4.6.2 Cook Log, Ratings & Tweak Notes ("I Made This!")

- Completing Cook Mode (or clicking **"Log a Cook"** on a recipe or calendar entry) records a `CookLog` entry:
  - `cookedAt` date, `cookedByUserId` (attributed to the signed-in household member), optional **Star Rating (`1–5`)**, **Cook's Notes** (e.g., _"Doubled the lime zest, baked 4 mins shorter — perfection"_), and optional dish photos.
  - Updates denormalized `timesCooked`, `lastCookedAt`, and household rating summary on the `Recipe`, surfacing the latest tweak notes prominently at the top of the recipe next time you cook it.

---

### 4.7 Discovery, "Use Up Ingredients" Matcher & External Share Links

#### 4.7.1 Smart Search & Faceted Discovery

- **Global Instant Search (`Cmd/Ctrl + K` on desktop, sticky search bar on mobile):** Searches across recipe titles, descriptions, canonical `FoodItem` names + aliases, tags, collections, source authors/books, and Cook Log notes.
- **High-Signal Household Filters:**
  - **Quick Weeknight:** `Total Time <= 30 min` or `<= 45 min`.
  - **Equipment & Method:** `Air Fryer`, `Instant Pot`, `Sheet Pan`, `Grill`, `One-Pot`, `Slow Cooker`.
  - **Course, Cuisine & Dietary** facets.
  - **"Forgotten Favorites":** Recipes rated `>= 4 stars` (or cooked `>= 2 times`) whose `lastCookedAt` is older than `60+ days`—answering _"What's something we love that we haven't made in ages?"_

#### 4.7.2 "Use Up Ingredients" Fridge Matcher

- Select 2–5 `FoodItem`s you currently have on hand that need to be used up (e.g., `Cilantro`, `Chicken Thighs`, `Lime`, `Jalapeño`).
- LocalRoast ranks all published recipes by:
  1. **Matched On-Hand Ingredients** (`4 of 4 matched` $\rightarrow$ `3 of 4 matched`), and
  2. **Fewest Additional Non-Staple Ingredients Needed** (ignoring `isPantryStaple` items so recipes aren't penalized for needing salt, pepper, oil, or water).

#### 4.7.3 Print-Friendly Cards & Revocable Read-Only Share Links

- **Print View:** Clean, ink-friendly `@media print` stylesheet formatting title, metadata, scaled ingredients, and steps onto 1–2 crisp pages without navigation chrome.
- **External Share Links (`/share/<token>`):**
  - Any household user can generate (and revoke at any time, or set an optional expiration date on) a cryptographically random share token for a specific recipe.
  - Visiting `/share/<token>` renders an unauthenticated, read-only view of **only** that recipe (supporting client-side serving scaling, US/Metric toggle, and Print View) while exposing zero household user details, Cook Logs (optional toggle), other recipes, meal plans, or shopping lists.

---

## 5. UX & Responsive Design Principles

| Surface / Workflow        | Mobile UX Priority (`360px+` — Kitchen & Grocery Store)                                                                | Desktop / Tablet UX Priority (Planning & Curation)                                                                       |
| :------------------------ | :--------------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------- |
| **Navigation**            | Bottom navigation bar (`Cookbook`, `Meal Plan`, `Shop`, `Ingest` + Inbox badge) and sticky top search.                 | Collapsible left navigation rail + global `Cmd/Ctrl + K` command palette and drag-and-drop workspaces.                   |
| **Recipe Ingestion**      | 1-tap **"Paste URL"** or **"Snap Cookbook Pages"** camera capture that queues immediately in `< 1s`.                   | **Side-by-Side Split Review** (zoomable source PDF/photo on left, structured recipe & `FoodItem` editor on right).       |
| **Meal Planning**         | Vertical day-by-day agenda accordion with a slide-up **"On-Deck"** drawer and 1-tap _"Start Cooking"_ on Today's meal. | Full 7-day multi-slot grid alongside a persistent **"On-Deck This Week"** sidebar with drag-and-drop scheduling.         |
| **Shopping List**         | Thumb-friendly full-width flat check rows, live SignalR sync indicator, and quick item adder.                          | Meal-Plan-to-List generator with date-range picker, recipe provenance chips, and Pantry Staples verification drawer.     |
| **Cooking (`Cook Mode`)** | Full-screen wake-locked step focus, inline ingredient chips, and sticky floating countdown timer bar.                  | Two-column tablet/landscape counter layout: pinned scaled ingredients on left, oversized active steps + timers on right. |

---

## 6. Suggested Phased Delivery Roadmap

To align with our `.agents/skills/plan-phase` and `/execute-phase` workflow (breaking each milestone into 2–3 focused vertical-slice phases), delivery is organized into four milestones:

```mermaid
flowchart LR
    M1["Milestone 1: Core Cookbook, Food Catalog, Scaling & Share Links"] --> M2["Milestone 2: Local LLM Ingestion Queue, Vision/PDF & MCP Core"]
    M2 --> M3["Milestone 3: Meal Plan Calendar, Smart Shopping Lists & SignalR Sync"]
    M3 --> M4["Milestone 4: Hands-Free Cook Mode, Cook Logs & 1-Click AI Helpers"]
```

- **Milestone 1 — Core Cookbook, Canonical Food Catalog & Discovery:**
  - Replace template `Todo` slice; implement Canonical `FoodItem` catalog (aisles, aliases, `isPantryStaple` seed data), structured `Recipe` CRUD (ingredient/step sections, ranges, inline step timers, Sub-Recipe DAG linking), local `/data/uploads` WebP image pipeline, dynamic scaling + fraction/unit conversion, Collections & Tags, Smart Search + **"Use Up Ingredients"** matcher, Print View, and revocable `/share/<token>` public recipe links.
- **Milestone 2 — Local LLM Ingestion Pipeline, Vision/PDF & Embedded MCP Server:**
  - In-App LLM Provider Settings UI (`Ollama` + `OpenAI-compatible /v1` with model discovery & health checks), persistent `IngestionJob` SQL queue & background worker with offline-host retry resilience, Side-by-Side **Review Inbox**, Web URL + Raw Text + Multi-Page Camera Image + PDF ingestion with auto-enrichment (timers, tags, shopping categories), Personal Access Tokens (`lr_pat_...`), and `/api/mcp` Streamable HTTP server exposing cookbook & ingestion tools.
- **Milestone 3 — Meal Planning Calendar, Smart Shopping Lists & Real-Time Co-Shopping:**
  - Multi-slot Weekly/Monthly Meal Plan Calendar (multi-dish slots, custom serving overrides, `Leftovers` shopping exclusion, freeform notes, **"On-Deck"** flexible weekly tray, reusable Week Templates), Multiple Named Flat Shopping Lists with unit-consolidated generation + recipe provenance badges + Pantry Staples separation, `SignalR` real-time co-shopping sync, and expanding `/api/mcp` with meal-plan and shopping-list tools.
- **Milestone 4 — Hands-Free Cook Mode, Cook Logs & 1-Click AI Kitchen Helpers:**
  - Wake-locked **Cook Mode** (`navigator.wakeLock`, inline step ingredient callouts, floating multi-timer bar with alerts), **Cook Log** (_"I Made This!"_ history, star ratings, tweak notes, dish photos, and _"Forgotten Favorites"_ discovery filter), `FoodItem` duplicate-merge management tools, and focused LLM helpers (_Ingredient Substitutions_, _Draft Week Meal Plan_, and _Ask Kitchen AI_).
