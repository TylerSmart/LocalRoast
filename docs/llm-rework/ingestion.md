Building a reliable recipe ingestion engine across both frontier models (OpenAI) and local models (Ollama) requires **separating LLM extraction from database resolution and validation**.

Sending a single massive prompt with all your database tags and food items will quickly blow past context limits, degrade reasoning accuracy (especially on smaller local Ollama models), and cost too much in tokens. Conversely, spinning up an **MCP (Model Context Protocol) server** so the LLM can query your database mid-generation introduces unnecessary network complexity and relies on the model choosing to call tools reliably—something 7B–14B parameter Ollama models frequently botch when they also have to output complex nested JSON.

The most resilient architecture is a **deterministic multi-stage pipeline orchestrated in .NET** (using `Microsoft.Extensions.AI`), where C# controls the workflow, performs fast local candidate lookups, and uses structured JSON outputs for the LLM calls.

| Architectural Approach | Pros | Cons | Verdict |
| --- | --- | --- | --- |
| **Single Mega-Prompt** | One round-trip; simple C# code. | Prompts grow infinitely as DB grows; models lose focus when juggling extraction, entity matching, and tokenization at once. | **Avoid** |
| **LLM with MCP / Tool Callbacks** | LLM only queries the DB for items it sees in the recipe. | Local Ollama models struggle to chain multiple tool calls *and* return strict JSON schemas; high latency from multiple round-trips. | **Overkill for internal APIs** |
| **.NET Orchestrated Pipeline** | Keeps prompts tiny; works reliably with local Ollama models; C# enforces strict validation and retries. | Requires orchestrating 2–3 distinct steps in your .NET backend service. | **Recommended** |

---

## 1. Resolving Food Items and Tags Without Giant Prompts

To prevent having four variations of "apples" on your shopping list without stuffing 5,000 database items into your system prompt, use an **Extract → Search → Disambiguate** workflow in .NET.

### Step 1: Separate the "Shopping Item" from the "Preparation"

When the LLM reads raw recipe text, instruct it to split every ingredient line into two distinct concepts:

* **Display Ingredient**: What the cook needs for the recipe (`"2 large Granny Smith apples, peeled and diced"`).
* **Proposed Canonical Food Item**: The base item you actually buy in the produce aisle (`"Granny Smith apple"` or `"apple"`, depending on how granular you want your pantry database to be) plus a proposed grocery category (`"Produce"`).

### Step 2: Candidate Retrieval in .NET (No LLM Needed)

Once the LLM returns the list of proposed food items and tags, your .NET backend queries your database for close matches before creating anything new:

* **Exact / Normalized Match**: Normalize strings (lowercase, singularize via a library like `Humanizer`, strip punctuation). If `"apple"` matches an existing `FoodItem` in your database, link its `FoodItemId` immediately in C#.
* **Fuzzy / Vector Search**: If there is no exact match, run a fast similarity search in your database (e.g., PostgreSQL `pg_trgm` trigram similarity or `pgvector` embeddings) to grab the **top 5 closest existing items** for each unresolved ingredient and tag.

### Step 3: Targeted Micro-Disambiguation Prompt

If your fuzzy search finds candidates with medium confidence (for example, the recipe asks for `"scallions"` and your DB has `"green onion"`, `"yellow onion"`, and `"shallot"`), make a lightweight second LLM call that includes **only those candidates**:

```json
{
  "unresolvedIngredient": "1 bunch scallions, sliced",
  "proposedCanonical": "scallion",
  "databaseCandidates": [
    { "id": 42, "name": "Green Onion" },
    { "id": 108, "name": "Yellow Onion" },
    { "id": 215, "name": "Shallot" }
  ]
}

```

Ask the LLM to either return the matching `id` (`42`) or return `null` so your .NET backend knows to insert a brand-new `FoodItem`. The exact same pattern works for **Tags**: let the LLM propose 5 tags, fuzzy-search your `Tags` table for the closest existing tags, and pass only the top 15–20 relevant existing tags in a quick resolution step.

---

## 2. Tokenizing Instructions for Scaling, Timers, and Temperatures

If you want Angular to dynamically scale ingredient amounts inside the text of a step, launch interactive timers, and toggle between Fahrenheit and Celsius, **do not ask the LLM to write raw HTML or complex inline markdown syntax**. Models frequently hallucinate closing tags or break syntax when scaling divided ingredients (e.g., when the ingredient list calls for *2 tbsp olive oil, divided*, and Step 1 uses *1 tbsp* while Step 3 uses *1 tbsp*).

Instead, use **Structured Outputs (JSON Schema)** where each step contains a clean template string with placeholders (`{0}`, `{1}` or named tokens like `{{ing_1}}`) alongside a strongly typed array of token metadata.

### Recommended JSON Schema Contract

Here is how you can structure the C# data contracts for the LLM output using `Microsoft.Extensions.AI`:

```csharp
public record ExtractedRecipeDto(
    string Title,
    int Servings,
    List<string> ProposedTags,
    List<ExtractedIngredientDto> Ingredients,
    List<ExtractedStepDto> Steps,
    RecipeQualityAuditDto QualityAudit
);

public record ExtractedIngredientDto(
    string RefId,                // e.g., "ing_1"
    string CanonicalFoodItem,    // e.g., "unsalted butter"
    double? Quantity,            // e.g., 4
    string? Unit,                // e.g., "tbsp"
    string? PreparationNotes,    // e.g., "melted, divided"
    string RawText               // Original line from source
);

public record ExtractedStepDto(
    int StepNumber,
    // Example: "Melt {{ing_1_a}} in a skillet over medium heat. Bake at {{temp_1}} for {{timer_1}}."
    string TemplateText,
    List<StepIngredientTokenDto> IngredientTokens,
    List<StepTimerTokenDto> TimerTokens,
    List<StepTemperatureTokenDto> TemperatureTokens
);

public record StepIngredientTokenDto(
    string TokenKey,             // e.g., "{{ing_1_a}}"
    string IngredientRefId,      // Links back to "ing_1"
    double? StepQuantity,        // e.g., 2 (handles divided ingredients!)
    string? StepUnit,            // e.g., "tbsp"
    string FallbackDisplay       // e.g., "butter" (if no specific quantity in step)
);

public record StepTimerTokenDto(
    string TokenKey,             // e.g., "{{timer_1}}"
    int DurationSeconds,         // e.g., 1200 (20 minutes)
    string Label                 // e.g., "Bake chicken"
);

public record StepTemperatureTokenDto(
    string TokenKey,             // e.g., "{{temp_1}}"
    double Value,                // e.g., 400
    string BaseUnit              // "F" or "C"
);

public record RecipeQualityAuditDto(
    bool IsPlausibleRecipe,
    List<string> Warnings        // e.g., ["OCR may have misread 1/4 cup salt as 14 cups salt"]
);

```

### Why This Schema Solves Your Angular Frontend Challenges:

1. **Divided Ingredients & Scaling**: Because `StepIngredientTokenDto` has its own `StepQuantity` (`2` tbsp) linked to `ing_1` (`4` tbsp total), when a user scales a recipe by `2x` in Angular, your frontend multiplies both the master ingredient list (`4 * 2 = 8 tbsp`) and the inline step token (`2 * 2 = 4 tbsp`) accurately.
2. **F/C Conversion**: Angular receives `{ Value: 400, BaseUnit: "F" }` and a user preference setting. A simple Angular pipe converts `400°F` to `205°C` (or `200°C` rounded to standard oven increments) on the fly without mutating the original text.
3. **Clickable Timers**: Angular splits `TemplateText` on `{{...}}` tokens and renders `{{timer_1}}` as a clickable `<app-Timer-chip [seconds]="1200" [label]="'Bake chicken'">20 minutes</app-timer-chip>` component.

---

## 3. Deterministic Validation & Self-Correction Loop

LLMs occasionally drop an ingredient from the steps, invent a token key in `TemplateText` that doesn't exist in the token arrays, or botch math on divided ingredients. Because your output is strongly typed in C#, you should run a **deterministic C# validator** immediately after deserializing the LLM response.

### What Your C# Validator Should Check:

* **Orphaned Tokens**: Does every `{{...}}` placeholder in `TemplateText` have a corresponding entry in `IngredientTokens`, `TimerTokens`, or `TemperatureTokens`?
* **Broken References**: Does every `IngredientRefId` in `IngredientTokens` match a valid `RefId` in the top-level `Ingredients` list?
* **Unreferenced Ingredients**: Are there ingredients in the master list that are never mentioned in *any* step? (Sometimes recipes omit "salt and pepper to taste" in the steps, so treat this as a soft warning or prompt a quick check).
* **Quantity Sum Mismatch**: If `ing_1` has a total quantity of `2 cups`, and it is referenced across two steps with explicit step quantities of `1 cup` and `2 cups` (summing to `3 cups`), flag a math mismatch.

### The Self-Correction Retry Loop

When your C# validator catches structural errors, do not throw an exception to the user. Feed the exact C# validation errors back to the LLM in a follow-up message within the same conversation thread (capped at 2 retries):

```csharp
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, rawScrapedRecipeText)
};

for (int attempt = 0; attempt < 3; attempt++)
{
    var response = await chatClient.GetResponseAsync<ExtractedRecipeDto>(messages);
    var validationErrors = RecipeValidator.Validate(response.Result);

    if (validationErrors.Count == 0)
    {
        return response.Result; // Valid! Move to DB entity resolution.
    }

    // Append the flawed output and the specific compiler-like errors for self-correction
    messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
    messages.Add(new ChatMessage(
        ChatRole.User,
        $"Your JSON failed validation with the following errors:\n" +
        string.Join("\n", validationErrors.Select(e => $"- {e}")) +
        "\nPlease return the corrected JSON."
    ));
}

```

Even local 8B parameter models on Ollama fix 90%+ of missing token references on the first retry when given explicit error messages like:

`- Step 2 TemplateText contains token '{{ing_5}}', which is missing from Step 2 IngredientTokens.`

---

## 4. Culinary Quality Check (Catching Bad Scrapes & Hallucinations)

When users scrape URLs or upload PDFs/images via OCR, fraction slashes often get stripped (`"1/4 tsp cayenne"` becomes `"14 tsp cayenne"` or `"1 1/2 cups flour"` becomes `"112 cups flour"`), or half the instructions get cut off by an ad-blocker boundary.

You can handle culinary quality control in two ways depending on whether the user is running a fast cloud model or a slower local Ollama model:

1. **Inline Audit (Zero Extra Latency)**: Include the `QualityAudit` object right inside the extraction schema (as shown in `RecipeQualityAuditDto` above). Instruct the system prompt:
* *Check if any ingredient quantities look like OCR fraction errors (e.g., 12, 13, 14, 34 instead of 1/2, 1/3, 1/4, 3/4) based on culinary proportions.*
* *Check if any major raw protein or baking ingredient lacks a cooking step or temperature.*
* *Check if the input text is actually a recipe.*


2. **Angular "Staging & Review" UI (Human-in-the-Loop)**: Never auto-commit an LLM-imported recipe directly into the active meal plan without a quick visual confirmation step in Angular. When the .NET API returns the parsed recipe to the Angular frontend:
* Render the parsed recipe in an interactive **Review Mode**.
* If `QualityAudit.Warnings` contains any items (e.g., `"14 tsp salt looks unusually high; check if original source meant 1/4 tsp"`), display a warning banner above the ingredient in Angular with a one-click fix button.
* Show a badge next to any newly created `FoodItem` (e.g., `New item: "Gochujang"`) with a dropdown allowing the user to merge it into an existing item if they prefer.



---

## 5. Summary of the Complete .NET Ingestion Flow

1. **Ingest & Clean (.NET)**: Scrape URL (using `HtmlAgilityPack` or `AngleSharp`, stripping `<script>`, `<nav>`, and ads—and checking for `<script type="application/ld+json">` `schema.org/Recipe` metadata first, which gives you clean raw text for free on 80% of recipe websites!) or extract document text.
2. **Structured Extraction + Culinary Audit (LLM Call #1)**: Send cleaned text to OpenAI/Ollama using `IChatClient.GetResponseAsync<ExtractedRecipeDto>()`.
3. **Validation Loop (.NET + Conditional Retry)**: Run `RecipeValidator` in C#. If tokens or quantities don't add up, run a self-correction turn.
4. **Entity Resolution (.NET + Micro LLM Call #2 if needed)**: Match `CanonicalFoodItem` and `ProposedTags` against your DB using exact/fuzzy/vector search. Only query the LLM with a short list of ambiguous DB candidates if confidence is between 60% and 90%.
5. **Return to Angular**: Present the structured recipe with interactive tokens (`{{ing_X}}`, `{{timer_X}}`, `{{temp_X}}`) and any quality warnings for user approval.
