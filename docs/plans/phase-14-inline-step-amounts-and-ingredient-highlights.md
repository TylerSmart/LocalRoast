# Phase 14: Inline Step Measurement Insertion, Ingredient Highlights & Step Linking Consistency

> **Branch:** `feature/phase-14-inline-step-amounts-and-ingredient-highlights`
> **Status:** `[ ]` Not Started
> **PRD Reference:** `docs/prd.md` — Sections 4.1.4, 4.1.5, 4.2.1; `docs/todo.md` — Ingestion items 2, 3
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.
> **Live Environment Note:**
> - The local app (`http://localhost:4200`) and SQL Server (`localhost,14333` / container `local-roast-sqlserver`, DB `LocalRoast`) already contain the 1,562 imported recipes (including *Ultracreamy Hummus*). Do not wipe or drop the database.
> - The Ollama server runs at `http://ThinkStationP3:11434`. **Hardware Constraint:** Only ever send **1 LLM request at a time** (no parallel requests, and ensure the app's background ingestion queue is paused before running any live LLM check).

---

## 1. Scope & Objectives

1. **Rewrite/Insert Unstated Step Measurements During Ingestion:**
   - When an ingredient with a known measurement is actively added in a step without an explicit amount in the source step prose (either because that step is the only step using the ingredient, or because it receives the calculated remainder — e.g., Step 1 of *Ultracreamy Hummus*: `"Combine chickpeas, baking soda, and 6 cups water in medium saucepan..."`), `InstructionAmountTagger.Assemble` inserts the formatted measurement directly before the ingredient mention so the step text becomes `"Combine 2 cans chickpeas, 1/2 tsp baking soda, and 6 cups water in medium saucepan..."` with `InlineStepAmount` spans for each inserted amount.
2. **Highlight Both Ingredient Amount and Ingredient Name Separately:**
   - Extend `InlineStepAmount`, `RecipeStepInlineAmount`, `RecipeStepInlineAmountInput`, and `RecipeStepInlineAmountDto` with optional `IngredientSpanStart` and `IngredientSpanLength` offsets indexing the ingredient name immediately associated with that measurement (e.g., `[2 cans]` and `[chickpeas]`, `[1/2 tsp]` and `[baking soda]`, `[6 cups]` and `[water]`).
   - Do **not** highlight or insert amounts for passive references where an already-combined ingredient is merely mentioned in passing (e.g., Step 2 `"While chickpeas cook..."`, Step 3 `"Drain chickpeas..."`, Step 5 `"Add meat... to mushrooms"`).
3. **Consistent Step Ingredient Linking & "Divided" Breakdown (*Ultracreamy Hummus* Step 2):**
   - Fix `InstructionAmountTagger.Assemble`'s mention fallback so ingredients actively combined in a step (e.g., `salt` and `lemon juice` in Step 2 `"Whisk lemon juice, salt, and reserved garlic together"`) are linked to that step even if another step also uses `salt` later (`"Season with salt"`).
   - Fix `ingredientUses` and `recipe-detail` / `public-shared-recipe` / `recipe-detail-preview` so `"Divided: ..."` only appears when an ingredient is genuinely divided across **2 or more steps** (`uses.length >= 2`) or when a single step's amount is strictly less than the row's total amount (never showing `"Divided: 1/3 cup (step 2)"` when Step 2 uses the entire `1/3 cup` row).

---

## 2. Domain & Database Design (`Core/` & `Data/`)

### 2.1 Entities & EF Core Configuration (`Data/Entities/RecipeStepInlineAmount.cs` & `Data/Configurations/RecipeStepInlineAmountConfiguration.cs`)

- Add two nullable integer properties to `RecipeStepInlineAmount`:
  ```csharp
  public int? IngredientSpanStart { get; set; }
  public int? IngredientSpanLength { get; set; }
  ```
- Migration:
  ```powershell
  dotnet ef migrations add AddInlineIngredientNameSpans --project Data/App.Data.csproj
  ```

### 2.2 DTOs & Contracts (`Core/Recipes/RecipeModels.cs` & `Core/Ingestion/IngestionModels.cs`)

- Update `InlineStepAmount` (`Core/Ingestion/IngestionModels.cs`):
  ```csharp
  public record InlineStepAmount(
      int Start,
      int Length,
      string Text,
      InlineStepAmountKind Kind,
      int? IngredientIndex,
      RecipeMeasurement Measurement,
      RecipeMeasurement? AlternativeMeasurement = null,
      int? IngredientSpanStart = null,
      int? IngredientSpanLength = null
  );
  ```
- Update `RecipeStepInlineAmountDto` & `RecipeStepInlineAmountInput` (`Core/Recipes/RecipeModels.cs`):
  ```csharp
  public record RecipeStepInlineAmountDto(
      int Start,
      int Length,
      InlineStepAmountKind Kind,
      Guid? IngredientId,
      RecipeMeasurement Measurement,
      RecipeMeasurement? AlternativeMeasurement = null,
      int? IngredientSpanStart = null,
      int? IngredientSpanLength = null
  );

  public record RecipeStepInlineAmountInput(
      [Range(0, 3999)] int Start,
      [Range(1, 200)] int Length,
      InlineStepAmountKind Kind,
      int? IngredientIndex,
      [Required] RecipeMeasurement Measurement,
      RecipeMeasurement? AlternativeMeasurement = null,
      [Range(0, 3999)] int? IngredientSpanStart = null,
      [Range(1, 200)] int? IngredientSpanLength = null
  );
  ```

### 2.3 Service Business Logic (`Core/Ingestion/InstructionAmountTagger.cs` & `Data/Services/RecipeService.cs`)

1. **`Core/Ingestion/InstructionAmountTagger.cs`:**
   - **Active vs. Passive Mention Detection:**
     - Add helper `IsPassiveReference(string stepText, Match mentionMatch, bool alreadyUsedInEarlierStep)`:
       - Returns `true` when `alreadyUsedInEarlierStep` is `true` and the clause before `mentionMatch.Index` starts with `\b(?:while|as|until|once|after|when|drain|rinse|return|transfer)\b` or `(?:to|over|into|with)\s+(?:the\s+|warm\s+|hot\s+|cooled\s+|cooked\s+)?$` (e.g., `"While chickpeas cook"`, `"Drain chickpeas"`, `"Add meat... to mushrooms"`).
     - For active mentions of ingredients in a step (where `!IsPassiveReference(...)`), ensure the ingredient index is added to `step.Indices` even if that ingredient is also used in a later step (fixing `salt` in Step 2 of *Ultracreamy Hummus*).
     - Remove passive-only mentions from `step.Indices` when the ingredient was already added in full in an earlier step (e.g., if `chickpeas` is added in full in Step 1, Step 3 `"Drain chickpeas"` does not count as an ingredient addition step unless a reserved portion is measured there).
   - **Single-Use & Remainder Amount Assignment + Step Text Insertion:**
     - If a measured ingredient `i` (`Quantity > 0`) is linked to **exactly 1 step** and has no `InlineStepAmount` in that step, assign `step.Amounts[i]` to the ingredient's full row measurement (just as lines 516–559 already do for a single unstated remainder step).
     - For each step, for every ingredient `i` in `step.Amounts` that does **not** yet have an `InlineStepAmount` in `step.Inline`:
       - Locate the first non-overlapping match of `MentionPattern(ingredients[i])` in `stepText`.
       - Format the measurement using `CulinaryMath.ScaleAndConvert(amount.Quantity, amount.MaxQuantity, amount.Unit, amount.CustomUnitName, 1m, UnitSystem.Original)` (e.g., `"2 cans"`, `"1/2 tsp"`, `"1/3 cup"`).
       - Insert `$"{formattedAmount} "` into `stepText` at `mentionMatch.Index`.
       - Shift `Start` and `IngredientSpanStart` of all existing spans in `step.Inline` whose `Start >= mentionMatch.Index` by `insertedPrefix.Length`.
       - Add a new `InlineStepAmount` with:
         - `Start = mentionMatch.Index`
         - `Length = formattedAmount.Length`
         - `Text = formattedAmount`
         - `Kind = InlineStepAmountKind.Ingredient`
         - `IngredientIndex = i`
         - `Measurement = amount`
         - `IngredientSpanStart = mentionMatch.Index + insertedPrefix.Length`
         - `IngredientSpanLength = mentionMatch.Length`
   - **Attach `IngredientSpanStart` / `IngredientSpanLength` to Existing Explicit Amounts:**
     - For explicit `InlineStepAmount` items where `Kind == InlineStepAmountKind.Ingredient` (e.g., `"1 tablespoon garlic"`) or `Mixture` (`"6 cups water"` where `"water"` immediately follows `"6 cups "`):
       - Match the immediately following ingredient name or `"water"` after the amount span (skipping optional `"of "` or adjectives like `"whole "`, `"minced "`, `"warm "`) and set `IngredientSpanStart` and `IngredientSpanLength`.

2. **`Data/Services/RecipeService.cs` & `Core/Ingestion/RecipeDraftNormalizer.cs`:**
   - Map `IngredientSpanStart` and `IngredientSpanLength` through `RecipeDraftNormalizer.NormalizeInlineAmounts` (shifting by `leadingTrim` alongside `Start`, and clearing `IngredientSpanStart`/`IngredientSpanLength` if out of bounds or overlapping).
   - Validate and persist `IngredientSpanStart` and `IngredientSpanLength` on `RecipeStepInlineAmount` in `RecipeService.cs`.

---

## 3. API Endpoints

Existing recipe and ingestion endpoints automatically serialize `ingredientSpanStart` and `ingredientSpanLength` on `RecipeStepInlineAmountDto` and `RecipeStepInlineAmountInput`.

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Utilities (`src/app/core/models/recipe.models.ts` & `src/app/core/utils/step-instruction.ts`)

- **`src/app/core/models/recipe.models.ts`:**
  - Add `ingredientSpanStart?: number | null;` and `ingredientSpanLength?: number | null;` to `RecipeStepInlineAmountDto` and `RecipeStepInlineAmountInput`.
- **`src/app/core/utils/step-instruction.ts`:**
  - Update `InstructionSegment`:
    ```ts
    export interface InstructionSegment {
      text: string;
      amount?: {
        kind: InlineStepAmountKind;
        ingredientId: string | null;
        ingredientName: string | null;
        scaled: boolean;
      };
      ingredientHighlight?: {
        kind: InlineStepAmountKind;
        ingredientId: string | null;
        ingredientName: string | null;
      };
    }
    ```
  - In `buildInstructionSegments`:
    - When an `InlineStepAmountDto` has a valid non-overlapping `ingredientSpanStart` and `ingredientSpanLength` (`ingredientSpanStart >= amount.start + amount.length`), emit:
      1. The amount segment (`segment.amount`),
      2. The intervening whitespace/text slice (`text.slice(amountEnd, ingredientSpanStart)`),
      3. The highlighted ingredient name segment (`segment.ingredientHighlight`).
  - In `relocateInlineAmounts`:
    - Relocate `ingredientSpanStart` alongside `start` when the step text is edited; if the ingredient text no longer matches, clear `ingredientSpanStart` / `ingredientSpanLength` on that span without dropping the amount span.
  - **Fix `ingredientUses` & `"Divided"` logic (`step-instruction.ts` & `recipe-detail.component.ts`):**
    - Add helper `isIngredientDivided(steps, ingredientId, ingredients, factor, system): boolean`:
      - Returns `true` iff `uses.length >= 2` OR (`uses.length === 1` and the single use's quantity in the row's unit is strictly less than the row's scaled quantity minus `1%` tolerance).
      - Update `recipe-detail.component.ts`, `public-shared-recipe.component.ts`, and `recipe-detail-preview.component.ts` to only show `Divided: ...` when `isIngredientDivided(...)` is `true`.

### 4.2 Component Rendering (`src/app/shared/components/step-instruction/`)

- **`step-instruction.component.html`:**
  ```html
  @for (seg of segments(); track $index) {
    @if (seg.amount; as amount) {
      @if (amount.kind === 'Fixed') {
        <span class="fixed-amount" data-kind="Fixed">{{ seg.text }}</span>
      } @else {
        <span
          class="inline-amount"
          [class.is-scaled]="amount.scaled"
          [attr.data-kind]="amount.kind"
          [attr.title]="amount.ingredientName ?? (amount.kind === 'Mixture' ? 'Mixture' : null)"
          >{{ seg.text }}</span
        >
      }
    } @else if (seg.ingredientHighlight; as ing) {
      <span
        class="inline-ingredient"
        [attr.data-kind]="ing.kind"
        [attr.title]="ing.ingredientName"
        >{{ seg.text }}</span
      >
    } @else {
      {{ seg.text }}
    }
  }
  ```
- **`step-instruction.component.scss`:**
  - Style `.inline-ingredient` distinctly from `.inline-amount`:
    ```scss
    .inline-ingredient {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      border-radius: 6px;
      padding: 0 5px;
      font-weight: 500;
    }
    ```

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/TaggedInstructionAmountsE2ETests.cs`)

Update/add scenarios in `TaggedInstructionAmountsE2ETests.cs`:
- `[TestMethod] InlineAmounts_RecipeDetail_HighlightsBothAmountAndIngredientNameSeparately()`:
  - Verify that a step with `Start`/`Length` on `"2 cans"` and `IngredientSpanStart`/`IngredientSpanLength` on `"chickpeas"` renders both `.inline-amount` (`"2 cans"`, scaling to `"4 cans"` at `2x`) and `.inline-ingredient` (`"chickpeas"`) as separate highlighted chips, while passive text `"While chickpeas cook"` in Step 2 has no chip.
- `[TestMethod] InlineAmounts_SingleUseIngredient_DoesNotShowDividedBadge()`:
  - Verify that an ingredient used in full in a single step does not render `"Divided:"` under its ingredient row, whereas an ingredient split across Step 1 and Step 2 renders `"Divided: ... (step 1) · ... (step 2)"`.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core, Data & Migration**
  - Add `IngredientSpanStart` and `IngredientSpanLength` to `RecipeStepInlineAmount`, `InlineStepAmount`, `RecipeStepInlineAmountInput`, and `RecipeStepInlineAmountDto`.
  - Update `InstructionAmountTagger.Assemble` to:
    1. Link active unmeasured/multi-step ingredients and exclude passive references (`"While chickpeas cook"`).
    2. Inject unstated single-use and remainder measurements into the step instruction text before the ingredient mention.
    3. Populate `IngredientSpanStart` and `IngredientSpanLength` for both injected and explicit step amounts.
  - Update `RecipeDraftNormalizer`, `TaggedStepDiagnostics`, and `RecipeService` to validate, shift, and persist `IngredientSpanStart` and `IngredientSpanLength`.
  - Generate and format EF Core migration:
    ```powershell
    dotnet ef migrations add AddInlineIngredientNameSpans --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write & run unit and integration tests:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 2: Angular Client & Spec Tests**
  - Update `recipe.models.ts`, `step-instruction.ts`, `step-instruction.component.{html,scss}`, and `recipe-detail` / `public-shared-recipe` / `recipe-detail-preview` `"Divided"` checks.
  - Update `step-instruction.spec.ts` and component specs.
  - Run frontend verification in `Client/`:
    ```powershell
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 3: Playwright E2E & Final Formatting**
  - Update `Tests/Client.E2ETests/TaggedInstructionAmountsE2ETests.cs` and run E2E tests:
    ```powershell
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format all backend and frontend files and run full solution test suite:
    ```powershell
    dotnet csharpier format .
    dotnet test App.slnx
    ```
  - Mark this plan's status as `[x] Completed`.
