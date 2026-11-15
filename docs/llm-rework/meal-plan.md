Generating a meal plan from a natural language prompt ("I want cozy fall dinners, something Mexican on Tuesday, under 30 minutes on weeknights, and use up the spinach in my fridge") works best as a **three-stage RAG (Retrieval-Augmented Generation) pipeline in .NET**: **Query Decomposition → Hybrid Database Retrieval → Constrained Scheduling**.

Because you normalized your `FoodItem`s and `Tags` during recipe ingestion, your database already has the exact structure needed to filter recipes deterministically before the LLM ever builds the schedule.

---

## 1. Prepare Recipes for Fast Retrieval (At Save Time)

You never want to send full recipe steps or massive ingredient lists to the LLM during meal planning. When a recipe is saved to your database, compute and store two lightweight fields on your `Recipe` entity:

1. **A Compact "Planning Card" Projection**:
* `RecipeId`, `Title`, `TotalTimeMinutes`, `Servings`
* `Tags`: `["Mexican", "Quick", "Weeknight"]`
* `CoreIngredients`: The canonical `FoodItem` names (excluding pantry staples like salt, water, and cooking oil), e.g., `["chicken thigh", "cilantro", "lime", "black bean"]`.
* `LastPlannedDate`: Crucial so the AI doesn't schedule the exact same meals you ate last week.


2. **A Search Embedding (Optional but Recommended)**:
* Concatenate the title, tags, and core ingredients into a single string (e.g., `"Sheet Pan Chicken Fajitas. Tags: Mexican, Quick, Sheet Pan. Ingredients: chicken breast, bell pepper, onion, lime."`) and generate a vector embedding using `IEmbeddingGenerator<string, Embedding<float>>` in `Microsoft.Extensions.AI` (supported by both OpenAI `text-embedding-3-small` and Ollama `nomic-embed-text`).



---

## 2. The 3-Stage Meal Planning Pipeline in .NET

Instead of giving the LLM a database search tool via MCP and hoping a local Ollama model executes 5–6 sequential search calls properly, keep C# in control of the workflow.

| Stage | Who Does the Work | What Happens |
| --- | --- | --- |
| **1. Decompose Mood Prompt** | LLM (Call #1) | Translates the user's freeform prompt into structured hard filters, must-have ingredients, day-specific rules, and 2–4 distinct search queries. |
| **2. Candidate Retrieval** | .NET + Database | Runs fast SQL/vector queries per search sub-query, excludes forbidden ingredients, and retrieves 25–35 compact "Recipe Planning Cards" (~1,500 tokens total). |
| **3. Assemble Schedule** | LLM (Call #2) | Reviews the 25–35 candidate cards alongside the user's prompt, selects the best 5–7 recipes, assigns them to days, plans leftovers, and minimizes grocery waste. |

---

### Stage 1: Translate the User's Prompt into Search Criteria

If a user says, *"We're in the mood for a couple of Italian pasta dishes, one Asian stir-fry, and something slow-cooked on Sunday. No seafood, and I have a bunch of spinach to use up,"* a single vector search for that entire paragraph will produce muddy results.

Instead, have the LLM parse the prompt into a structured `MealPlanRequestAnalysisDto`:

```csharp
public record MealPlanRequestAnalysisDto(
    // Hard exclusions matched against your Canonical FoodItems / Tags
    List<string> ExcludedIngredientsOrTags, // e.g., ["seafood", "shrimp", "salmon", "fish"]

    // Ingredients the user already has and wants to prioritize
    List<string> PrioritizeFoodItems,       // e.g., ["spinach"]

    // Distinct search buckets so every craving gets candidates from the DB
    List<RecipeSearchQueryDto> SearchBuckets,

    // Day-specific constraints extracted from the prompt
    List<DayConstraintDto> DayConstraints
);

public record RecipeSearchQueryDto(
    string BucketName,          // e.g., "Italian pasta dishes"
    string SemanticSearchText,  // e.g., "Italian pasta dinner comfort food"
    List<string> SuggestedTags, // e.g., ["Italian", "Pasta"]
    int? MaxPrepTimeMinutes,    // e.g., 45
    int DesiredCount            // e.g., 2 (we need 2 meals for this bucket)
);

public record DayConstraintDto(
    DayOfWeek Day,
    stringMealType MealType,    // Dinner, Lunch, etc.
    int? MaxTimeMinutes,        // e.g., 30 for "busy Wednesday"
    string? SpecificCraving     // e.g., "Slow-cooked meal" on Sunday
);

```

---

### Stage 2: Candidate Retrieval & "Ingredient Synergy" in C#

Now your .NET backend takes `MealPlanRequestAnalysisDto` and queries the database **deterministically**:

1. **Apply Hard SQL Filters First**:
Exclude any recipe linked to a `FoodItem` or `Tag` in `ExcludedIngredientsOrTags`. Because you normalized `"shrimp"` and `"salmon"` in your database during recipe import, a simple `.Where(r => !r.Ingredients.Any(i => excludedIds.Contains(i.FoodItemId)))` guarantees 100% safety against unwanted ingredients.
2. **Query Per Bucket**:
For each `RecipeSearchQueryDto` in `SearchBuckets`, pull the top `DesiredCount * 4` recipes using vector similarity (or full-text/tag matching). Fetching 4x what you need gives the scheduler room to optimize.
3. **Add "Pantry Clearer" & "Ingredient Synergy" Candidates**:
* Query for 5 recipes that use the user's `PrioritizeFoodItems` (e.g., `"spinach"`).
* **Pro-Tip for Grocery Lists**: In C#, you can slightly boost the score of candidate recipes that share perishable `FoodItemId`s (like fresh herbs, heavy cream, cabbage, or spinach) with your top candidates.


4. **Deduplicate and Format as Compact Cards**:
Combine the results into a deduplicated list of 25–35 `CandidateRecipeCardDto` objects:

```json
[
  {
    "recipeId": 104,
    "title": "Tuscan Creamy Garlic Chicken with Spinach",
    "totalMinutes": 30,
    "servings": 4,
    "tags": ["Italian", "Quick", "Chicken"],
    "coreIngredients": ["chicken breast", "spinach", "heavy cream", "parmesan", "garlic"],
    "daysSinceLastMade": 24
  },
  {
    "recipeId": 219,
    "title": "Spicy Pork & Green Bean Stir-Fry",
    "totalMinutes": 25,
    "servings": 4,
    "tags": ["Asian", "Stir-Fry", "Quick"],
    "coreIngredients": ["ground pork", "green bean", "ginger", "scallion", "soy sauce"],
    "daysSinceLastMade": 40
  }
]

```

*(30 of these cards take up less than 1,500 tokens—meaning even a local 8B Ollama model will process them in a couple of seconds with zero context bloat.)*

---

### Stage 3: Constrained Schedule Assembly (LLM Call #2)

Now send the user's original prompt, the target calendar slots (e.g., Monday–Sunday Dinners), and the JSON array of 30 `CandidateRecipeCardDto` items to the LLM.

Instruct the system prompt to act as a meal planner that:

* **ONLY selects `recipeId`s from the provided candidate list** (never invents a recipe ID).
* Matches `totalMinutes` and tags to day-specific constraints (e.g., fast recipes on busy weeknights, slow recipes on Sunday).
* **Minimizes grocery list bloat** by picking recipes that share perishable `coreIngredients` (e.g., pairing two recipes that both use `"spinach"` or `"heavy cream"`).
* Supports **"Cook Once, Eat Twice" (Leftovers)** if the user wants to cook fewer nights (e.g., assigning `recipeId: 104` on Monday with `ScaleMultiplier: 2.0`, and marking Tuesday as `IsLeftoverFromDay: Monday`).

#### C# Structured Output for the Final Schedule:

```csharp
public record GeneratedMealPlanDto(
    List<PlannedMealSlotDto> Slots,
    List<string> SharedIngredientHighlights, // e.g., "Spinach is used on Monday and Thursday"
    List<string> UnmetCravingNotes           // e.g., "You asked for Thai curry, but no matching recipes were found in your library."
);

public record PlannedMealSlotDto(
    DayOfWeek Day,
    string MealType,              // "Dinner"
    int? SelectedRecipeId,        // Must match a CandidateRecipeCardDto.RecipeId
    double ScaleMultiplier,       // Usually 1.0, or 2.0 if cooking extra for leftovers
    bool IsLeftover,              // True if eating a previous day's meal
    string SelectionReasoning     // Short UI tooltip: "25-min stir-fry for busy Wednesday"
);

```

---

## 3. Handling Edge Cases & Validation in .NET

Just like recipe ingestion, run a quick C# validation pass on `GeneratedMealPlanDto` before returning it to Angular:

1. **Hallucinated ID Check**: Verify every `SelectedRecipeId` exists in the `CandidateRecipeCardDto` hash set you passed in. If the LLM invented an ID or duplicated a recipe unintentionally (when `IsLeftover == false`), trigger a 1-turn retry or swap in the next highest-scored candidate from that bucket.
2. **What if the User's Library Doesn't Have What They Asked For?**
Notice the `UnmetCravingNotes` field in `GeneratedMealPlanDto`. If a user only has 20 recipes in their database and asks for "7 days of authentic Moroccan food", Stage 2 won't find enough matches. By giving the LLM an explicit `UnmetCravingNotes` array (and allowing `SelectedRecipeId` to be `null` with a `SuggestedNewRecipeIdea` string), the LLM won't force-fit a pancake recipe into a Moroccan dinner slot. In Angular, you can render an **"AI Suggestion: Generate or Import a Moroccan Tagine recipe for Thursday"** button right inside that calendar slot!

---

## 4. Making the UX Shine in Angular (Zero-Cost Swaps)

Nobody accepts a 7-day AI meal plan 100% as-is on the first try—there is always one night where the user thinks, *"Meh, I don't feel like pork stir-fry on Wednesday."*

You can make swapping instantaneous in Angular **without making another LLM call**:

* When your .NET API returns the `GeneratedMealPlanDto` to Angular, **also return the remaining ~20 unused `CandidateRecipeCardDto` items** grouped by bucket (or ranked by match score).
* In Angular, add a **"Swap / Reroll"** button on each day's card, plus a **"Lock"** toggle on days the user loves.
* If the user clicks **Swap** on Wednesday, they can either pick immediately from a drawer of the top 5 runner-up candidates that already matched their mood prompt, OR click **"Ask AI to Re-plan Unlocked Days"** (which sends only the unlocked days back to Stage 3 with the locked recipes fixed in place so the AI can still optimize shared ingredients).

---

## 5. How This Flows Directly Into Your Shopping List

Because Stage 3 outputs `SelectedRecipeId` and `ScaleMultiplier` (and explicitly flags `IsLeftover: true` so you don't double-buy ingredients for leftover nights!):

1. Your .NET shopping list generator queries all `RecipeIngredient` rows for non-leftover slots.
2. It multiplies each `Quantity` by the slot's `ScaleMultiplier`.
3. It groups by your canonical `FoodItemId`, converts compatible units (e.g., `4 tbsp butter` + `1/2 cup butter` = `3/4 cup butter`), and hands the user a clean, consolidated grocery list.
