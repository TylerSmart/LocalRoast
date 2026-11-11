# Phase 18: Inline JSON Step Tokens & Simplified Ingestion

> **Branch:** `feature/phase-18-inline-json-step-ingredients`
> **Status:** Completed (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Section 4.1.4 (Ordered Steps, Inline Ingredient References & Timers) & Section 4.3.1 (LLM Ingestion)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

### 1.1 Problem Statement
The current step-ingredient highlighting architecture relies on character offsets (`RecipeStepInlineAmount.Start`, `Length`, `IngredientSpanStart`, `IngredientSpanLength`) and client-side fuzzy string tokenizers (`ingredientMentions` in `step-instruction.ts`). This produces severe operational issues:
1. **False Positives & False Negatives:** Substring heuristics constantly match common words like "corn" (in "peppercorns" or "cornstarch") and "sugar" (in "cook until sugar dissolves") even when not intended as ingredients.
2. **Fragile Offsets:** When a user edits step text in the Review UI, character offsets break or shift, requiring complex `relocateInlineAmounts` heuristics.
3. **Bloated Ingestion Pipeline:** `InstructionAmountTagger.cs` spans over 3,300 lines of regexes, candidate generators, and two-pass LLM tagging loops.
4. **Cluttered Review UX:** Below each step textarea, users are forced to manage an inline amounts table, a multi-select for "ingredients used in this step", and duplicate measurement sub-forms.
5. **Loss of Culinary Context:** Existing systems cannot distinguish between **recipe total consumption** (e.g., 4 teaspoons of butter for 4 pancakes) and **contextual step display** (e.g., 1 teaspoon per pancake).

### 1.2 Solution Summary
Transition `RecipeStep.Instruction` to embed **inline JSON tokens** using standard delimiters `||{ ... }||`:
- **Single Source of Truth:** The step instruction string contains both human prose and structured tokens (e.g., `Top each of the 4 pancakes with ||{"ingredient": "butter", "amount": 4, "unit": "teaspoon", "displayAmount": 1, "displayUnit": "teaspoon"}||.`).
- **Standard JSON Serialization:** Native `JSON.parse` in TypeScript and `JsonSerializer` in .NET eliminate custom mini-languages and regex scanners.
- **Contextual Presentation:** Supports total consumption (`amount`, `unit`) for recipe scaling and shopping lists alongside contextual display (`displayAmount`, `displayUnit`) for per-item instructions.
- **Unified Inline Timers & Temps:** Steps can also embed timer tokens: `||{"type": "timer", "seconds": 600, "label": "Simmer sauce"}||`.
- **Delightful Review & Edit UX:** A clean step editor with `@` autocomplete, a `+ Ingredient` / `+ Timer` dialog modal, and a live interactive preview where clicking any chip opens the dialog.
- **Zero Backward-Compatibility Debt:** Drop `RecipeStepInlineAmount` entity and SQL table, remove `InstructionAmountTagger.cs`, and delete fuzzy string tokenizers.

---

## 2. Domain & Database Design (`Core/` & `Data/`)

### 2.1 Enums & Token Types (`Core/Recipes/`)
Create `Core/Recipes/StepTokenModels.cs`:

```csharp
namespace App.Core.Recipes;

public enum StepTokenType
{
    Ingredient = 1,
    Timer = 2,
    Temperature = 3
}

public abstract record StepToken(StepTokenType Type);

public sealed record StepIngredientToken(
    string Ingredient,
    Guid? IngredientId = null,
    decimal? Amount = null,
    MeasurementUnit? Unit = null,
    string? CustomUnitName = null,
    decimal? DisplayAmount = null,
    MeasurementUnit? DisplayUnit = null,
    string? DisplayCustomUnitName = null,
    string? DisplayText = null,
    string? Notes = null
) : StepToken(StepTokenType.Ingredient);

public sealed record StepTimerToken(
    int Seconds,
    string? Label = null
) : StepToken(StepTokenType.Timer);

public sealed record StepTemperatureToken(
    int? Fahrenheit = null,
    int? Celsius = null
) : StepToken(StepTokenType.Temperature);

public sealed record StepInstructionSegment(
    string Text,
    StepToken? Token = null
)
{
    public bool IsToken => Token != null;
}
```

### 2.2 Core Token Parser (`Core/Recipes/StepInstructionParser.cs`)
Create a fast, resilient parser in `App.Core.Recipes`:
- **Regex:** `\|\|(?<json>\{.*?\})\|\|` (compiled with 1-second timeout).
- **`ParseSegments(string? instruction)`:** Splits instruction into ordered `StepInstructionSegment` records. If JSON deserialization fails on a token, it falls back gracefully to raw text rather than throwing.
- **`ExtractTokens(string? instruction)`:** Returns all parsed `StepToken` instances.
- **`ToPlainText(string? instruction)`:** Formats tokens into clean human-readable text for TTS, search indexing, or export (using `DisplayAmount`/`DisplayUnit` or `Ingredient`).
- **`FormatToken(StepToken token)`:** Serializes a `StepToken` into the `||{...}||` string representation with compact JSON settings.

### 2.3 Entity & Context Updates (`Data/`)
1. **Delete Entity & Configuration:**
   - Remove `Data/Entities/RecipeStepInlineAmount.cs`.
   - Remove `Data/Configurations/RecipeStepInlineAmountConfiguration.cs`.
2. **Update `Data/Entities/RecipeStep.cs`:**
   - Remove `public ICollection<RecipeStepInlineAmount> InlineAmounts { get; set; }`.
   - Keep `RecipeStep.Instruction` (string, max 4000).
   - Keep `Timers` and `IngredientReferences` as optional relational navigation helpers or clean up redundant fields.
3. **Update `Data/Context/AppDbContext.cs`:**
   - Remove `DbSet<RecipeStepInlineAmount> RecipeStepInlineAmounts`.
   - Remove `builder.ApplyConfiguration(new RecipeStepInlineAmountConfiguration());`.
4. **EF Core Migration:**
   - Name: `DropInlineAmountsAndAdoptStepJsonTokens`.
   - Drops `RecipeStepInlineAmounts` table and foreign keys.

### 2.4 DTOs & Service Contracts (`Core/`)
1. **Update `Core/Recipes/RecipeModels.cs`:**
   - Remove `RecipeStepInlineAmountDto`, `RecipeStepInlineAmountInput`.
   - Remove `InlineAmounts` and `IngredientAmounts` from `RecipeStepDto` and `RecipeStepInput`.
   - `RecipeStepDto` instruction contains the embedded `||{...}||` tokens:
     ```csharp
     public record RecipeStepDto(
         Guid Id,
         int StepNumber,
         string? SectionName,
         string Instruction,
         string? ImagePath,
         IReadOnlyList<Guid> IngredientIds,
         IReadOnlyList<RecipeStepTimerDto> Timers
     );
     ```
2. **Update `Core/Ingestion/IngestionModels.cs`:**
   - Remove `InlineAmounts` and `IngredientAmounts` from `LlmExtractedStepItem` and `IngestionDraftStepDto`.
   - `LlmExtractedStepItem` simply carries `Instruction` with embedded tokens:
     ```csharp
     public record LlmExtractedStepItem(
         int StepNumber,
         string Instruction,
         string? SectionName = null,
         string? ImageUrl = null
     );
     ```

### 2.5 Ingestion & Service Logic (`Data/Services/` & `Infrastructure/Ingestion/`)
1. **Deprecate / Remove `InstructionAmountTagger.cs`:**
   - Delete `Core/Ingestion/InstructionAmountTagger.cs` (~3,324 lines of candidate regexes and tagging heuristics).
2. **Update `Data/Services/RecipeService.cs`:**
   - Remove all includes, deletes, and mappings of `InlineAmounts`.
   - When saving steps, `Instruction` is stored as provided with `||{...}||` tokens.
   - Automatically synchronize `IngredientReferences` and `Timers` from the parsed tokens in `Instruction` so relational search and legacy queries continue to work seamlessly.
3. **Update `Infrastructure/Ingestion/LlmExtractionClient.cs`:**
   - Update text and vision recipe extraction prompts to directly emit `||{...}||` tokens within step instructions:
     ```text
     In the step instructions, embed all ingredient references and countdown timers as inline JSON tokens enclosed in ||{...}||.
     - For ingredients: ||{"ingredient": "name", "amount": total_number, "unit": "unit_name", "displayAmount": step_portion_number, "displayUnit": "unit_name"}||
     - If the step uses a portion (e.g., "Top each of 4 pancakes with 1 tsp butter"), set amount to the total used across the step (4) and displayAmount to the per-item amount (1).
     - If an ingredient is referenced without an amount (e.g., "fold in egg whites"), set amount to null: ||{"ingredient": "egg whites"}||
     - For timers: ||{"type": "timer", "seconds": 300, "label": "Simmer"}||
     ```
   - Eliminate the separate candidate tagging LLM request pass.

---

## 3. API Endpoints (`API/Controllers/`)

All existing controller routes remain intact, with simplified DTO payloads:

| Verb | Route (`/api/...`) | Request Body / Query | Response | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/recipes/{id}` | None | `RecipeDetailDto` | Steps contain `instruction` with `||{...}||` tokens. |
| `POST` | `/api/recipes` | `CreateRecipeRequest` | `RecipeDetailDto` | Validates step instructions. |
| `PUT` | `/api/recipes/{id}` | `UpdateRecipeRequest` | `RecipeDetailDto` | Updates step instructions. |
| `GET` | `/api/ingestion/drafts/{id}` | None | `IngestionDraftDto` | Steps carry embedded tokens. |
| `PUT` | `/api/ingestion/drafts/{id}` | `UpdateIngestionDraftRequest` | `IngestionDraftDto` | Persists edited draft step tokens. |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Token Utilities (`Client/src/app/core/`)
1. **Create `src/app/core/models/step-token.models.ts`:**
   ```typescript
   export type StepTokenType = 'ingredient' | 'timer' | 'temperature';

   export interface StepIngredientToken {
     type?: 'ingredient';
     ingredient: string;
     ingredientId?: string | null;
     amount?: number | null;
     unit?: string | null;
     customUnitName?: string | null;
     displayAmount?: number | null;
     displayUnit?: string | null;
     displayCustomUnitName?: string | null;
     displayText?: string | null;
     notes?: string | null;
   }

   export interface StepTimerToken {
     type: 'timer';
     seconds: number;
     label?: string | null;
   }

   export interface StepTemperatureToken {
     type: 'temperature';
     fahrenheit?: number | null;
     celsius?: number | null;
   }

   export type StepToken = StepIngredientToken | StepTimerToken | StepTemperatureToken;

   export interface StepSegment {
     text: string;
     token?: StepToken;
     rawToken?: string;
   }
   ```
2. **Create `src/app/core/utils/step-token-parser.ts`:**
   - `parseStepInstruction(instruction: string): StepSegment[]`: Uses regex `\|\|(\{.*?\})\|\|` with `JSON.parse` in a `try/catch` block.
   - `serializeStepToken(token: StepToken): string`: Returns `||${JSON.stringify(token)}||`.
   - `formatTokenDisplay(token: StepIngredientToken, factor: number, system: UnitSystem): string`: Calculates scaled display value:
     - Scales `token.displayAmount ?? token.amount` by `factor`.
     - Applies unit conversion (Metric/US) using existing `culinary-math.ts`.
   - `stepInstructionToPlainText(instruction: string): string`: Strips tokens for plain-text presentation.
3. **Clean Up `src/app/core/utils/step-instruction.ts`:**
   - Remove `ingredientMentions`, `ingredientAliases`, `tokenizeWords`, `validInlineSpans`, and `relocateInlineAmounts`.

### 4.2 Shared Components (`Client/src/app/shared/components/`)
1. **Create `StepTokenDialogComponent`** (`step-token-dialog/`):
   - Standalone Angular Material Dialog (`MatDialog`).
   - Tabs or mode toggle: **Ingredient** vs. **Timer**.
   - **Ingredient Tab:**
     - Master ingredient autocomplete / dropdown (from available recipe ingredients).
     - Total Amount (`amount`) & Unit (`unit`).
     - Checkbox: *"Show different amount in step (e.g. per-pancake or divided)"*.
       - When checked: reveals `displayAmount` and `displayUnit`.
     - Optional notes/override text.
   - **Timer Tab:**
     - Duration in minutes/seconds.
     - Label input.
   - Returns serialized token string or structured `StepToken`.
2. **Create `StepIngredientChipComponent`** (`step-ingredient-chip/`):
   - Inline pill rendering in step text.
   - Displays scaled formatted amount and ingredient name.
   - Tooltip shows context: *"1 tsp per pancake (4 tsp total for recipe)"*.
   - Emits `(click)` event when in editable mode to open `StepTokenDialogComponent`.
3. **Delete `InlineAmountEditorComponent`** (`Client/src/app/shared/components/inline-amount-editor/`).

### 4.3 Feature Component Updates
1. **Ingestion Review (`ingestion-review.component.{html,ts,scss}`)**:
   - In each step card:
     - `mat-form-field` textarea for the raw instruction.
     - Quick action buttons above textarea: `[ + Ingredient ]` and `[ + Timer ]` (opens dialog and inserts at cursor).
     - Live interactive preview below textarea: renders parsed text with `StepIngredientChipComponent`.
     - Clicking any chip in preview opens `StepTokenDialogComponent` to edit or delete the token.
     - Remove `app-inline-amount-editor` and redundant measurement inputs.
2. **Recipe Detail & Preview (`recipe-detail-preview.component.{html,ts,scss}`, `recipe-detail.component.{html,ts,scss}`)**:
   - Renders step instruction segments using `parseStepInstruction`.
   - Ingredient tokens render as styled chips displaying scaled amounts.
   - Timer tokens render as clickable chips that start kitchen timers.
3. **Cook Mode (`cook-mode.component.{html,ts,scss}`)**:
   - Renders step instruction segments with high-contrast, large touch-target chips.
   - Clicking a timer token chip triggers `KitchenTimerService.startTimer(...)`.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/StepTokensE2ETests.cs`)

Create `Tests/Client.E2ETests/StepTokensE2ETests.cs` inheriting `E2ETestBase`:

- `[TestMethod] StepTokens_PancakePerItemDisplay_ScalesCorrectlyInCookMode()`:
  1. Register/login test user and seed a test recipe "Grandma's Pancakes" with step:
     `"Top each of the 4 pancakes with ||{\"ingredient\": \"butter\", \"amount\": 4, \"unit\": \"teaspoon\", \"displayAmount\": 1, \"displayUnit\": \"teaspoon\"}||."`
  2. Navigate to recipe detail and open Cook Mode.
  3. Verify step text displays: `"Top each of the 4 pancakes with [ 1 tsp butter ] ."`.
  4. Change servings multiplier to 2x (8 servings).
  5. Verify step chip dynamically updates to: `"[ 2 tsp butter ]"`.
  6. Hover or click chip to verify tooltip displays `"8 tsp total"`.

- `[TestMethod] StepTokens_ReviewAndEditInUI_DialogUpdatesToken()`:
  1. Open Ingestion Review page for a draft recipe.
  2. In Step 1, click `+ Ingredient` button.
  3. In `StepTokenDialogComponent`, select "Eggs", amount `4`, unit `whole`, check custom display, set display amount `2`.
  4. Click "Insert Token".
  5. Verify step textarea receives `||{"ingredient":"Eggs","amount":4,"unit":"whole","displayAmount":2,"displayUnit":"whole"}||`.
  6. Verify live preview renders chip `[ 2 whole Eggs ]`.
  7. Click the chip in preview, edit amount to `6`, and save.
  8. Verify token in textarea updates to `amount: 6`.

- `[TestMethod] StepTokens_MobileViewport_360px_RendersCorrectly()`:
  1. Set viewport to `360x640`.
  2. Open Cook Mode for the test recipe.
  3. Verify step instruction text and inline ingredient chips wrap naturally without horizontal overflow.
  4. Verify chip click target meets accessibility standards (minimum 44px height).

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend Core & Data Layer**
  - Create `Core/Recipes/StepTokenModels.cs` and `Core/Recipes/StepInstructionParser.cs`.
  - Update `Core/Recipes/RecipeModels.cs` and `Core/Ingestion/IngestionModels.cs` to remove `RecipeStepInlineAmount` contracts.
  - Delete `Data/Entities/RecipeStepInlineAmount.cs` and `Data/Configurations/RecipeStepInlineAmountConfiguration.cs`.
  - Update `Data/Entities/RecipeStep.cs` and `Data/Context/AppDbContext.cs`.
  - Delete `Core/Ingestion/InstructionAmountTagger.cs`.
  - Update `Data/Services/RecipeService.cs` and `Data/Services/IngestionReviewService.cs`.
  - Generate, apply, and format EF Core migration:
    ```powershell
    dotnet ef migrations add DropInlineAmountsAndAdoptStepJsonTokens --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Create and run unit tests in `Tests/Core.UnitTests/StepInstructionParserTests.cs`:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    ```

- [x] **Task 2: Ingestion Prompts & API Integration Tests**
  - Update `Infrastructure/Ingestion/LlmExtractionClient.cs` prompts to emit `||{...}||` tokens for steps.
  - Update API controllers and request mappers.
  - Update and run integration tests in `Tests/API.IntegrationTests/`:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 3: Angular Models, Token Parser, Dialog, and Feature Components**
  - Create `Client/src/app/core/models/step-token.models.ts` and `Client/src/app/core/utils/step-token-parser.ts`.
  - Clean up `Client/src/app/core/utils/step-instruction.ts` (delete fuzzy alias and regex offset logic).
  - Create `Client/src/app/shared/components/step-token-dialog/` (`.ts`, `.html`, `.scss`, `.spec.ts`).
  - Create `Client/src/app/shared/components/step-ingredient-chip/` (`.ts`, `.html`, `.scss`, `.spec.ts`).
  - Delete `Client/src/app/shared/components/inline-amount-editor/`.
  - Update `ingestion-review.component.{html,ts,scss}` with the new token editor and live preview.
  - Update `recipe-detail.component.{html,ts}`, `recipe-detail-preview.component.{html,ts}`, and `cook-mode.component.{html,ts}` to use `step-token-parser`.
  - Run frontend lint, build, and unit tests:
    ```powershell
    cd Client
    npm run lint
    npm run build:production
    npm test -- --watch=false
    cd ..
    ```

- [x] **Task 4: Playwright E2E Verification & Final Formatting**
  - Implement `Tests/Client.E2ETests/StepTokensE2ETests.cs` with the scenarios from Section 5.
  - Run Playwright E2E tests:
    ```powershell
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format full solution:
    ```powershell
    dotnet csharpier format .
    cd Client
    npm run format
    npm run lint:fix
    cd ..
    ```
  - Run full test suite:
    ```powershell
    dotnet test App.slnx
    ```
  - Mark plan status as `[x] Completed`.
