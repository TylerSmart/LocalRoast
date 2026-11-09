# Phase 13.2: Advanced Step Reconciliation, Per-Item Multipliers & Source Line Repair

## 1. Objective & Scope

Following Phase 13.1, auto-publish accuracy across the 1,381 processed recipes in `LocalRoast` reached **97.1%** (1,341 Published, 40 ReadyForReview; 91.2% of requeued review jobs auto-published). Detailed inspection of all 40 remaining `ReadyForReview` jobs showed that **39 of the 40** stem from 5 deterministic categories in [`InstructionAmountTagger.cs`](../../Core/Ingestion/InstructionAmountTagger.cs), [`RecipeDraftNormalizer.cs`](../../Core/Ingestion/RecipeDraftNormalizer.cs), [`RecipeExtractionDiagnostics.cs`](../../Core/Ingestion/RecipeExtractionDiagnostics.cs), and [`LlmExtractionClient.cs`](../../Infrastructure/Ingestion/LlmExtractionClient.cs) (with the 40th, *Best Beef Stew*, being a genuinely truncated source document that stops at Step 4):

1. **Source-Line Synthesis, Compound `"X plus Y"` Measurements & Split `"A or B"` Deduplication (7 recipes):**
   - Floating-point precision mismatch (`1m / 3m == 0.3333333333333333m` is `false`) in [`InstructionAmountTagger.RestoreSourceMeasurement`](../../Core/Ingestion/InstructionAmountTagger.cs) prevents clearing hallucinated `MaxQuantity` values from parenthetical weights like `"1/3 cup (2 1/3 ounces) sugar"` (*Oatmeal Scones*).
   - Single-row extraction of compound `"<qty1> <unit1> plus <qty2> <unit2>"` source lines (*Julia Child's Stuffed Turkey*, *Shrimp Pad Thai*) drops or mis-adds the smaller unit.
   - [`InstructionAmountTagger.IsSplitAlternative`](../../Core/Ingestion/InstructionAmountTagger.cs) fails to merge LLM-split `"A or B"` rows when the model appends the shared head noun (`cheese`, `oil`, `oregano`) to both rows (*Quick Cheese Bread*, *Stir-Fried Pork, Eggplant, and Onion*, *Simple Italian-Style Meat Sauce*).
   - When the LLM omits a bulleted source ingredient line during initial extraction (*Osso Buco*: `- 2 cups chicken broth`), [`InstructionAmountTagger.RepairIngredientRows`](../../Core/Ingestion/InstructionAmountTagger.cs) does not synthesize the missing row before method tagging runs.
2. **`AlternativeMeasurement`-First Unit Conversion & Produce Conversions (3 recipes):**
   - [`InstructionAmountTagger.TryToRowUnit`](../../Core/Ingestion/InstructionAmountTagger.cs) checks generic `4 oz = 1 cup` cheese density *before* checking `ingredient.AlternativeMeasurement`, causing lighter grated cheeses with explicit volume equivalents on the row (`1 oz Parmesan (1/2 cup)` in *Cheese Soufflé*, `1 1/2 oz Asiago (about 3/4 cup)` in *Streamlined French Onion Soup*) to fail conversion.
   - Sliced scallions (`Whole` vs `Cup` with `"all but 1/4 cup... remaining 1/4 cup"` in *Pad Thai*) and wilted leafy greens (`12 oz` vs `1/3 cup` + `"remaining mustard greens"` in *Saag Paneer*) need count/sub-portion reconciliation.
3. **Per-Item / Per-Batch Multipliers & Rate/Conditional Phrases (15 recipes):**
   - Instructions that apply an amount to **"each `<item>`"** or **"each batch"** (*Baked Sole Fillets*, *Chicken Schnitzel*, *Eggs Benedict*, *Fried Whole Branzino*, *Salt-Baked Potatoes*, *Skillet-Roasted Fish Fillets*, *Mu Shu Pork*, *Blueberry Pancakes*, *Moussaka*, *Streamlined French Onion Soup*, *Garlic and Thyme Sauce*) record only the single-item amount instead of multiplying by the item/batch count.
   - Rate increments (`"<amount> ... at a time"`, `"every 2 to 3 minutes"`) and conditional top-ups (`"up to <amount>"`, `"if the liquid in the pan has totally evaporated"`) double-count or overflow rows (*Aligot*, *Jamaican Stew Peas with Spinners*, *Roast Turkey for a Crowd*, *Mushroom Risotto*).
4. **Multi-Row Spillover, Rendered Pan Fat, Re-Measured Prep Items & Unstated Remainder Linking (12 recipes):**
   - Multi-row ingredients of the same food (*Beef Burgundy*, *Beef Stir-Fry with Bell Peppers*, *Stir-Fried Chicken with Bok Choy*) fail when step spans split across multiple amounts instead of rolling over or reconciling across all rows of that food.
   - Rendered pan fat (`"Pour/Drain off all but X fat"`, `"reserved 1 tablespoon fat"`, `"Add enough oil to fat... to equal 2 tablespoons"`) and drained bean-soaking/rice-boiling salt overflow fresh oil and table salt rows (*Braised Chicken Thighs*, *Italian Chicken Soup with Parmesan Dumplings*, *Paella*, *Best Vegetarian Chili*, *Chicken Biryani*).
   - Re-measured pre-cooked/reserved portions (`"reserved 1 tablespoon butter"`, `"Wrap each scallop with 1 slice bacon"` after pre-cooking all 12 slices) double-count (*French Omelets*, *Grilled Bacon-Wrapped Scallops*, *Grilled Stuffed Pork Tenderloin*), and unlinked remainder steps mentioning `"add the oil"` miss remainder assignment (*Steak Diane*).
5. **Quality Warning Deduplication, `"to taste"` Auto-Linking & Divided-Ingredient Arithmetic Auto-Heal (2 recipes):**
   - [`RecipeExtractionDiagnostics.GetQualityWarnings`](../../Core/Ingestion/RecipeExtractionDiagnostics.cs) resets `reportedFoods` on every step, penalizing a single unlinked `"Table salt"` row multiple times (`-0.03` per step) and pushing zero-issue recipes like *Mechouia (Tunisian-Style Grilled Vegetables)* (`0.82`) below the `0.85` auto-publish threshold.
   - Source-text arithmetic typos on `, divided` ingredients where steps explicitly state every portion ending with `"remaining <amount>"` (*Osso Buco*: `3/8 tsp pepper, divided` vs `1/2 tsp` + `remaining 1/8 tsp` = `5/8 tsp`) are blocked from self-healing because `RestoreSourceMeasurement` and `RecipeDraftNormalizer` overwrite the row quantity back to `0.375`.

---

## 2. Exact File Paths to Modify

| Layer | File Path | Action |
| :--- | :--- | :--- |
| **Core** | `Core/Ingestion/SourceMeasurementParser.cs` | Modify: Add helper `TrySumCompoundLeadingMeasurement(string? text)` for `"<qty1> <unit1> plus <qty2> <unit2>"` source lines and `TryParseSourceIngredientLine(string line, string? sectionName)` to deterministically build an `LlmExtractedIngredientItem` from a missed bullet line. |
| **Core** | `Core/Ingestion/InstructionAmountTagger.cs` | Modify: Fix decimal tolerance in `RestoreSourceMeasurement`, broaden `IsSplitAlternative`, synthesize missing source lines in `RepairIngredientRows`, check `AlternativeMeasurement` first in `TryToRowUnit`, support per-item/per-batch multipliers, rate/conditional phrases, rendered pan fat, multi-row food reconciliation, `"to taste"` auto-linking, and divided-ingredient `"remaining"` sum auto-heal. |
| **Core** | `Core/Ingestion/RecipeDraftNormalizer.cs` | Modify: Preserve auto-healed divided quantities and compound `"X plus Y"` sums, clear false `MaxQuantity` from parenthetical weights, and deduplicate LLM-split `"A or B"` rows while remapping step indices. |
| **Core** | `Core/Ingestion/RecipeExtractionDiagnostics.cs` | Modify: Move `reportedFoods` outside the per-step loop in `GetQualityWarnings` so an unlinked ingredient is warned at most once per recipe. |
| **Infrastructure** | `Infrastructure/Ingestion/LlmExtractionClient.cs` | Modify: Pass `context.SourceContent` to `InstructionAmountTagger.RepairIngredientRows` (updating `recipe` with repaired rows before `Assemble`) and update `TriageAndRepairRecipeAsync` hints. |
| **Data** | `Data/Services/IngestionProcessor.cs` | Modify: Pass repaired ingredient rows from `Assemble` / `RepairIngredientRows` consistently. |
| **Tests** | `Tests/Core.UnitTests/Ingestion/Phase132ReconciliationTests.cs` | Create: Comprehensive unit tests covering all 5 categories and real recipe regressions from the review inbox. |

*(No EF Core database schema migrations or Angular UI changes are required for Phase 13.2.)*

---

## 3. Detailed Implementation Specifications

### Task 1: Source-Line Synthesis, Compound `"X plus Y"` Parsing & `"A or B"` Deduplication

1. **`Core/Ingestion/SourceMeasurementParser.cs`:**
   - Add `public static RecipeMeasurement? TrySumCompoundLeadingMeasurement(string? text)`:
     - Match leading compound measurements of the form `^(?<m1>...)(\s+plus\s+(?<m2>...))+` (e.g., `"1 teaspoon plus 2 tablespoons minced fresh sage"`, `"3 tablespoons plus 1/8 teaspoon sugar"`).
     - When all matched measurements convert into the largest unit among them via `CulinaryMath.TryConvertQuantity` (e.g., `Tbsp`), return a single `RecipeMeasurement(Math.Round(totalInLargestUnit, 4), largestUnit, null, null)`.
   - Add `public static LlmExtractedIngredientItem? TryParseSourceIngredientLine(string rawLine, string? sectionName = null)`:
     - Clean leading `- ` / bullet characters.
     - Extract primary measurement using `TrySumCompoundLeadingMeasurement(line)` or the leading span from `FindSpans(line)` (if `span.Index == 0`).
     - Extract parenthetical alternative measurement via `FindAlternative(line)`.
     - Strip the leading measurement and parenthetical alternative to isolate the food phrase; split on the first comma (or `\b(?:divided|drained|minced|chopped|sliced|grated|peeled| rinsed|crushed|halved|trimmed|cut|beaten|thawed)\b`) into `displayName` / `canonicalFoodName` and `preparationNote`.
     - Return a populated `LlmExtractedIngredientItem` with `OriginalRawText = line`.

2. **`Core/Ingestion/InstructionAmountTagger.cs` (`RepairIngredientRows`, `RestoreSourceMeasurement`, `IsSplitAlternative`):**
   - Update signature:
     ```csharp
     public static IReadOnlyList<LlmExtractedIngredientItem> RepairIngredientRows(
         IReadOnlyList<LlmExtractedIngredientItem> rows,
         string? source = null
     )
     ```
   - In `RestoreSourceMeasurement`:
     - Replace `s.Measurement.Quantity == row.Quantity` with `s.Measurement.Quantity is { } sq && Math.Abs(sq - row.Quantity.Value) <= 0.02m` so repeating fractions (`1/3`, `2/3`) match their source span and clear any hallucinated `MaxQuantity` (such as `2.3333` from `"1/3 cup (2 1/3 ounces)"`).
     - If a source line is a single-row extraction (`rows.Count(r => SourceLine(r) == SourceLine(row)) == 1`) and `SourceMeasurementParser.TrySumCompoundLeadingMeasurement(SourceLine(row))` returns a summed `RecipeMeasurement` (and the source line does NOT split into two separate rows), set `row = row with { Quantity = compound.Quantity, MaxQuantity = null, Unit = compound.Unit.ToString(), CustomUnitName = null }`.
   - In `IsSplitAlternative(first, second)`:
     - When `SourceLine(first) == SourceLine(second)` and `OrSeparator.IsMatch(line)` (and the line does NOT match `\bplus\b` or `^table\s+salt\s+and\b`), strip words common to both `first` and `second` (such as `"cheese"`, `"oil"`, `"oregano"`, `"leaves"`) when checking whether `first`'s distinguishing words appear before `"or"` and `second`'s distinguishing words appear after `"or"`.
     - When merging `second` into `first`, if `second.Quantity is > 0 && (second.Quantity != first.Quantity || !string.Equals(second.Unit, first.Unit, StringComparison.OrdinalIgnoreCase))`, format the note as `$"or {FormatNumber(second.Quantity.Value)} {second.Unit?.ToLowerInvariant()} {second.DisplayName ?? second.CanonicalFoodName}"` (fixes *Simple Italian-Style Meat Sauce*: `"1 tablespoon minced fresh oregano leaves or 1 teaspoon dried oregano"`).
   - At the end of `RepairIngredientRows(rows, source)`:
     - When `!string.IsNullOrWhiteSpace(source)`, inspect `RecipeExtractionDiagnostics.GetSourceIngredientLines(source)`.
     - For any source line that has `0` matching rows in `result` (`NormalizeLine(r.OriginalRawText) == NormalizeLine(line)`), call `SourceMeasurementParser.TryParseSourceIngredientLine(line, currentSection)` and insert the synthesized row at the matching index so no source line is ever missing when method tagging runs.
   - In `RecipeDraftNormalizer.Normalize`:
     - Apply the same adjacent split-alternative deduplication if `raw` was not already deduplicated, remapping `IngredientIndices`, `IngredientAmounts`, and `InlineAmounts` on `raw.Steps` accordingly.

---

### Task 2: `AlternativeMeasurement`-First Unit Conversion & Produce Conversions

1. **`Core/Ingestion/InstructionAmountTagger.cs` (`TryToRowUnit` & `Reconcile`):**
   - In `TryToRowUnit`:
     - Move the `ingredient.AlternativeMeasurement` conversion block (and a fallback to `SourceMeasurementParser.FindAlternative(ingredient.OriginalRawText)?.Alternative`) **above** `if (IsShreddedOrGratedCheese(ingredient) || IsDicedMeatOrPancetta(ingredient))`.
     - Add produce count-to-volume conversion for `Whole` scallions / green onions: `1 whole scallion ≈ 2 tbsp (0.125 cup)` sliced.
   - In `Reconcile` (lines 841–863, `hasBulkStep && allSubPortions`):
     - Broaden `allSubPortions` from `ia.InTbsp is <= 4m` (`1/4 cup`) to `ia.InTbsp is <= 8m` (`1/2 cup`), OR exempt unit incompatibility when the step(s) explicitly use `"all but <amount> of the <ingredient>"` + `"remaining <amount> <ingredient>"` (or `"Transfer remaining <ingredient>"`), which consumes 100% of the row (*Pad Thai*, *Saag Paneer*).

---

### Task 3: Per-Item / Per-Batch Multipliers & Rate / Conditional Phrases

1. **Rate Phrases & Conditional Top-Ups (`InstructionAmountTagger.Assemble`):**
   - Define regexes:
     - `RatePhraseAfter`: `^\s*(?:[a-z]+\s+){0,4}at\s+a\s+time\b|^\s*(?:[a-z]+\s+){0,4}every\s+\d+`
     - `ConditionalTopUpClause`: `\bif\s+[^.;()]*?\b(?:dry|thick|thin|stiff|tight|evaporated|needed|necessary)\b|\badd\s+up\s+to\b|\bplus\s+(?:more|extra)\s+as\s+needed\b`
   - In `Assemble`:
     - When a step has multiple spans for the same ingredient `i` and one of those spans is followed by `RatePhraseAfter` (e.g., `"stir in 2 tablespoons milk at a time (up to 1/2 cup)"` in *Aligot*, or `"add up to 2 teaspoons water, 1/2 teaspoon at a time"` in *Jamaican Stew Peas*, or `"Add 3 1/2 cups of the broth... Stir in 1/2 cup more broth every 2 to 3 minutes"` in *Mushroom Risotto*), reclassify the rate span (`"2 tablespoons"`, `"1/2 teaspoon"`, `"1/2 cup"`) as `InlineStepAmountKind.Fixed` (`IngredientIndex = null`) and rebuild the step's amount for `i`.
     - During the overflow check (`total > rowHigh + tolerance`), if a span for row `i` occurs inside a `ConditionalTopUpClause` (or `row.OriginalRawText` contains `"plus more as needed"` / `"plus extra as needed"`), reclassify that overflowing conditional span as `InlineStepAmountKind.Fixed` (`IngredientIndex = null`) so unconditional usage is preserved without overflowing (*Aligot*, *Jamaican Stew Peas*, *Roast Turkey for a Crowd*).
2. **Per-Item / Per-Batch Multipliers (`InstructionAmountTagger.Assemble` & `Reconcile`):**
   - In `Assemble`:
     - **Multi-step batch/item multiplier:** If a step has an inline amount for row `i` and the sentence containing that amount matches `\b(?:toss|rub|brush|coat|season|sprinkle)\s+(?:the\s+)?(?:interior\s+of\s+)?each\s+(?<item>batch|sheet|pan|thigh|breast|half|piece|fillet|cutlet|side)\b` (e.g., *Moussaka*: `"Divide eggplant evenly between 2 prepared sheets. Toss each batch with 1/4 cup oil, 1/2 teaspoon salt, and 1/4 teaspoon pepper"`; *Julia Child's Stuffed Turkey*: `"Rub interior of each thigh with 1/2 teaspoon sage"`; *Fried Whole Branzino*: `"cut each fish in half... Brush both sides of each piece evenly with 1/2 tablespoon mustard"`):
       - Determine the batch/item multiplier $M \ge 2$ (from an explicit count in the step/previous sentence such as `2 prepared sheets`, `4 pieces`, or `2` for `thigh`/`batch`/`half`/`side`, or from the primary protein/vegetable ingredient row's `Quantity` when `Unit == Whole`).
       - If multiplying the step's `Amounts[i]` by $M$ brings the total across all steps for row `i` within `tolerance` of `rowHigh` (without exceeding `rowHigh + tolerance`), update `step.Amounts[i]` to the multiplied measurement!
     - **Single-step per-item / rate consumption:** If row `i` is only measured in **1 step** across the entire recipe, `sumHigh < rowLow`, no other step uses row `i` as an unmeasured remainder, and that step contains `\b(?:each|per|every|individual|at\s+a\s+time|in\s+batches)\b` (e.g., `"Spread 1/2 teaspoon mustard on each fillet"`, `"Sprinkle each cutlet on both sides with 3/4 teaspoon salt"`, `"Place 1 slice bacon on each English muffin"`, `"Brush the exposed portion of each potato with 1 teaspoon oil"`, `"Sprinkle 1/8 teaspoon sugar evenly over one side of each fillet"`, `"Spread about 1/2 teaspoon hoisin in center of each warm pancake"`, `"sprinkle 1 tablespoon of the blueberries over each pancake"`, `"Top each bowl... sprinkle with 2 tablespoons of the grated Asiago"`, `"whisk in butter, 1 tablespoon at a time"`):
       - Update `step.Amounts[i]` to the full row measurement (`rowLow`..`rowHigh` in `row.Unit`) so the step reflects the total ingredient used across all items while keeping the inline text span untouched.

---

### Task 4: Multi-Row Spillover, Rendered Pan Fat, Re-Measured Prep Items & Unstated Remainder Linking

1. **Multi-Row Spillover & Same-Food Group Reconciliation (`InstructionAmountTagger.Assemble` & `Reconcile`):**
   - In `Assemble` (lines 487–521):
     - When row `i` overflows (`total > rowHigh + tolerance`) and there is an unused measured sibling `j` of the same food (`FoodName(ingredients[j]) == FoodName(row)`):
       - In addition to checking whether a single span matches `sibling.Quantity`, check whether all spans of `i` in a **single step** sum to `sibling.Quantity` (fixes *Beef Burgundy* Step 8 where `1/2 cup + 1/4 cup = 3/4 cup water` matches `[21] (Garnish) 3/4 cup water`). If so, reassign all spans of `i` in that step to `j`.
   - In `Reconcile`:
     - Before emitting an overflow (`step amounts exceed the ingredient list`) or underflow (`steps use X of Y`) issue for row `i`, check if there are multiple measured rows sharing the same `FoodName(ingredient)` (e.g., *Beef Stir-Fry with Bell Peppers*: `3 tbsp` + `1 tsp vegetable oil`; *Stir-Fried Chicken with Bok Choy*: `1/4 cup` + `2 tbsp` + `2 tsp peanut oil`).
     - If the **sum of step amounts across all measured rows of that food** reconciles with the **sum of row quantities across all measured rows of that food** (converting everything into `Tbsp` or the first row's unit within 2% tolerance), suppress the false per-row split allocation error!
2. **Rendered Pan Fat, Reserved Portions & Drained Boiling Salt (`InstructionAmountTagger.Assemble`):**
   - When an oil/fat row (`IsOilOrFatIngredient(row)`) overflows (`total > rowHigh + tolerance`):
     - Reclassify any span where the step text around the span matches rendered pan fat or top-up phrasing: `\b(?:pour|drain|skim|spoon)\s+off\s+all\s+but\b`, `\breserved?\s+[^.;]*?\b(?:fat|butter|oil)\b`, `\bto\s+fat\b[^.;]*?\bto\s+equal\b`, or where the span text/noun is `"fat"` while the ingredient row is `"oil"` or `"butter"`, as `InlineStepAmountKind.Mixture` (`IngredientIndex = null`). (Fixes *Braised Chicken Thighs*, *Italian Chicken Soup with Parmesan Dumplings*, *Paella*, *French Omelets*).
   - When any ingredient row overflows (`total > rowHigh + tolerance`) because a later step references an already-prepped or reserved portion from an earlier step (`\breserved\b` before the span, or Step 1 already consumed 100% of `bacon` with `"remaining 6 slices"` and Step 2 says `"Wrap each scallop with 1 slice bacon"`), reclassify the later step's re-measurement span as `InlineStepAmountKind.Mixture` (`IngredientIndex = null`). (Fixes *French Omelets*, *Grilled Bacon-Wrapped Scallops*).
   - When a `salt` row overflows (`total > rowHigh + tolerance`) and one of its spans is in a step that boils water and drains (`\bdrain\b` in the same step or a later step for that component, as in *Best Vegetarian Chili* and *Chicken Biryani*), move that span to an unmeasured `salt` sibling row if present, or reclassify it as `InlineStepAmountKind.Fixed` (`IngredientIndex = null`).
3. **Broaden Batch Repetition & Unstated Remainder Linking (`InstructionAmountTagger.Assemble` & `Reconcile`):**
   - In `Reconcile` (`hasRepeatStep`, line 939): match `\brepeat(?:ing)?\b.*\b(?:second|other|remaining|next|each)\b|\brepeat(?:ing)?\s+(?:with\s+)?(?:the\s+)?remaining\b|\brepeat\s+step\b` so *French Omelets* (`"repeating instructions for second omelet"`) and *Grilled Stuffed Pork Tenderloin* (`"Repeat with remaining tenderloin, stuffing, and spinach"`) are recognized.
   - In `Assemble` (before the unstated remainder assignment loop at line 678):
     - If a measured ingredient `i` has `0 < sumHigh < rowHigh` and `steps.Count(s => s.Indices.Contains(i) && !s.Amounts.ContainsKey(i)) == 0`, check if any step without an amount for `i` mentions `MentionPattern(ingredients[i])` (excluding `"<food> mixture"`). If exactly one such step exists (or a step says `"add the <food>"` / `"remaining <food>"`), add `i` to that step's `Indices` so it receives the unstated remainder (fixes *Steak Diane*: `"Off the heat, add the oil and shallot to the skillet"`).

---

### Task 5: Quality Warning Deduplication, `"to taste"` Auto-Linking & Divided-Ingredient Auto-Heal

1. **`Core/Ingestion/RecipeExtractionDiagnostics.cs` (`GetQualityWarnings`):**
   - Move `var reportedFoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);` **before** `for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)` so each unlinked food is warned at most once per recipe (fixes triple-penalizing `salt` across Steps 1, 2, and 4 in *Mechouia*).
2. **`Core/Ingestion/InstructionAmountTagger.cs` (`Assemble` — `"to taste"` Auto-Linking & Divided Typo Auto-Heal):**
   - In `Assemble`:
     - For unmeasured ingredients (`ingredients[i].Quantity is null`), if a step's text matches `\b(?:season|sprinkle|serve|garnish)\b[^.;]*?\b(?:to\s+taste|for\s+serving|with\s+(?:the\s+)?lemon\s+wedges)\b` and matches `MentionPattern(ingredients[i])`, add `i` to `step.Indices` (auto-links `"season with salt and pepper to taste"` in *Mechouia* while leaving non-`"to taste"` phrases like `"Melt butter and add salt."` untouched for E2E repair testing).
     - Change `Assemble` to take `IList<LlmExtractedIngredientItem>` (or return updated `IReadOnlyList<LlmExtractedIngredientItem> Ingredients` on `TaggedMethodResult`) so that when an ingredient row is marked `\bdivided\b` in `OriginalRawText` or `PreparationNote`, `sumLow > rowHigh` in the same unit, the excess `sumLow - rowHigh` is $\le 1\text{ tbsp}$, and at least one step explicitly states `"remaining <amount>"` for that ingredient (such as *Osso Buco*'s `1/2 tsp pepper` + `remaining 1/8 tsp pepper` = `0.625 tsp` against `"3/8 teaspoon pepper, divided"`), `Assemble` updates `ingredients[i] = ingredients[i] with { Quantity = Math.Round(sumLow, 4), MaxQuantity = null }`.
     - In `RecipeDraftNormalizer.Normalize`: when `rawIng.Quantity is > 0` and `sourcePair` is `null`, `Normalize` already uses `rawIng.Quantity` directly, so the healed `0.625 tsp` flows straight into the normalized draft!

---

## 4. Verification & Ordered Execution Checklist

- [ ] **Task 1:** Implement `TrySumCompoundLeadingMeasurement` and `TryParseSourceIngredientLine` in `Core/Ingestion/SourceMeasurementParser.cs`, and update `RepairIngredientRows`, `RestoreSourceMeasurement`, `IsSplitAlternative`, and `RecipeDraftNormalizer.Normalize` to synthesize missing source lines, sum single-row `"X plus Y"` lines, fix repeating-fraction tolerance, and deduplicate split `"A or B"` rows.
- [ ] **Task 2:** Update `InstructionAmountTagger.TryToRowUnit` and `Reconcile` to prioritize `AlternativeMeasurement` before generic cheese density and handle `Whole` scallions / sub-portion greens with `"remaining"`.
- [ ] **Task 3:** Implement rate-phrase (`"at a time"`, `"every X minutes"`) and conditional top-up reclassification plus per-item/per-batch multiplier resolution in `InstructionAmountTagger.Assemble` and `Reconcile`.
- [ ] **Task 4:** Implement multi-row same-food spillover/reconciliation, rendered pan fat / reserved-portion / drained-boiling-salt handling, broadened batch repetition, and unstated-remainder step linking in `InstructionAmountTagger.Assemble` and `Reconcile`.
- [ ] **Task 5:** Deduplicate `reportedFoods` across steps in `RecipeExtractionDiagnostics.GetQualityWarnings`, auto-link `"to taste"` unmeasured seasonings, and auto-heal explicit `"remaining <amount>"` divided-ingredient arithmetic typos in `InstructionAmountTagger.Assemble`.
- [ ] **Task 6:** Create `Tests/Core.UnitTests/Ingestion/Phase132ReconciliationTests.cs` and run full verification:
  - `dotnet test Tests/Core.UnitTests/App.Core.UnitTests.csproj`
  - `dotnet test Tests/API.IntegrationTests/App.API.IntegrationTests.csproj`
  - `dotnet csharpier check .`
