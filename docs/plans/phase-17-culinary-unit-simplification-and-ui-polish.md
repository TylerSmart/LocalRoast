# Phase 17: Culinary Measurement Simplification & Mobile/Recipe Detail UI Polish

> **Branch:** `feature/phase-17-culinary-unit-simplification-and-ui-polish`
> **Status:** `[x]` Completed
> **PRD Reference:** `docs/prd.md` — Sections 4.1.2, 4.1.4, 4.1.6; `docs/todo.md` — Various items 2, 3, 4, 8, 9
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.
> **Live Environment Note:**
> - The local app is running at `http://localhost:4200` backed by `LocalRoast` on `localhost,14333` (`local-roast-sqlserver`), which already includes the *Blueberry Pie* recipe at `http://localhost:4200/recipes/d70c27f6-dfd2-4452-84c2-277823121962` and the *"The Complete America's Test Kitchen TV Show Cookbook"* collection. Do not wipe or drop the database.

---

## 1. Scope & Objectives

1. **Simplify Ingredient & Step Measurements When Scaling Recipes (`Core/Recipes/CulinaryMath.cs` & `Client/src/app/core/utils/culinary-math.ts`):**
   - When multiplying or halving a recipe (or converting to US Customary / Metric), automatically simplify US volume (`Tsp` $\leftrightarrow$ `Tbsp` $\leftrightarrow$ `Cup` $\leftrightarrow$ `Qt`) and weight (`Oz` $\leftrightarrow$ `Lb`) measurements into practical kitchen units:
     - **Step up:** e.g., `1 tbsp` $\times 4 = 4\text{ tbsp} \rightarrow 1/4\text{ cup}$; `2 tbsp` $\times 4 = 8\text{ tbsp} \rightarrow 1/2\text{ cup}$; `3 tsp` $\rightarrow$ `1 tbsp`; `16 oz` $\rightarrow$ `1 lb`.
     - **Step down:** e.g., `1/4 cup` $\times 0.5 = 1/8\text{ cup} \rightarrow 2\text{ tbsp}$ (since standard measuring cups start at `1/4 cup`); `1 tbsp` $\times 0.5 \rightarrow 1\text{ }1/2\text{ tsp}$; `1/4 lb` $\times 0.5 \rightarrow 2\text{ oz}$.
2. **Fix Sub-Recipe Ingredient Styling (*Blueberry Pie* — `recipe-detail` & `public-shared-recipe`):**
   - Fix `.ingredient-row` flex wrapping so `<mat-expansion-panel class="sub-recipe-inline-panel">` and `<p class="unresolved-recipe-reference">` render on their own full-width line below the ingredient row rather than getting squeezed horizontally next to the checkbox and action buttons.
   - Un-nest `.unresolved-recipe-reference` and `.unresolved-recipe-label` from `.sub-recipe-inline-panel` so unresolved sub-recipe notices receive their proper badge styling, and add complete `.sub-recipe-content` / `.sub-ingredients-list` styling in `recipe-detail.component.scss`.
3. **Fix Mobile Overflow on `.collection-pill-group` for Long Collection Names (`recipe-list.component.scss`):**
   - Override `flex-shrink: 0` on `.collection-pill` inside `.collection-pill-group` (`flex: 1 1 auto; flex-shrink: 1; min-width: 0;`) so long names like *"The Complete America's Test Kitchen TV Show Cookbook"* wrap cleanly without pushing the count badge and action buttons outside the pill group on `360px` mobile screens.
4. **Fix Oval Hover State on Collection & Tag Filter Action Buttons (`recipe-list.component.scss`):**
   - Change `.collection-pill-action` and `.tag-pill-action` from `width: 36px; height: 44px; border-radius: 50%` (a vertical ellipse) to equal square dimensions (`width: 34px; height: 34px; aspect-ratio: 1; border-radius: 50%; margin: 2px;`) so hover and focus states are true circles.
5. **Fix Hidden-Text Icon Button Margins on Narrow Viewports (`app-shell`, `cook-mode`, `source-viewer`):**
   - Remove leftover icon trailing/leading margins whenever button label text is hidden (`display: none`) on `[aria-label="User profile and account options"]` in `app-shell.component.scss`, `.exit-cook-btn` / `.narration-pill-btn` / `.voice-control-pill-btn` in `cook-mode.component.scss`, and `mat-button-toggle` / `.cover-action button` in `source-viewer.component.scss`.

---

## 2. Domain & Backend Design (`Core/Recipes/CulinaryMath.cs`)

### 2.1 Unit Simplification Rules (`Core/Recipes/CulinaryMath.cs`)

Update `ConvertUnit(decimal qty, decimal? maxQty, MeasurementUnit? unit, UnitSystem targetSystem)` (and pass `decimal scaleFactor = 1m` from `ScaleAndConvert`):
- When `unit is null`, return `(qty, maxQty, unit)`.
- When `targetSystem == UnitSystem.Original`:
  - If `scaleFactor == 1m`, keep `(qty, maxQty, unit)` unchanged so `1x` Original displays the author's exact unit.
  - If `scaleFactor != 1m`, run `SimplifyIntraSystemUnit(qty, maxQty, unit.Value)`.
- When `targetSystem == UnitSystem.UsCustomary`:
  - First convert metric units (`Ml`/`L` $\rightarrow$ `Tsp`/`Tbsp`/`Cup`/`Qt`, `G`/`Kg` $\rightarrow$ `Oz`/`Lb`).
  - Then run `SimplifyIntraSystemUnit(convQty, convMaxQty, convUnit.Value)` so native US Customary units also simplify!
- **`SimplifyIntraSystemUnit(decimal qty, decimal? maxQty, MeasurementUnit unit)` rules:**
  - Do not simplify ranges (`maxQty.HasValue && maxQty.Value > qty`) if both bounds do not simplify cleanly into the same target unit.
  - **US Volume (`Tsp = 1`, `Tbsp = 3`, `Cup = 48`, `Qt = 192`):**
    1. **`Cup` step-down (`qty < 0.25m`, i.e. `< 12 tsp`):**
       - Compute `tsp = qty * 48m`.
       - If `IsCleanMultiple(tsp, 1.5m)` (whole or half tablespoons, e.g. `1/8 cup = 6 tsp = 2 tbsp`, `1/16 cup = 3 tsp = 1 tbsp`), convert to `MeasurementUnit.Tbsp` (`tsp / 3m`).
       - Otherwise, if `IsCleanMultiple(tsp, 0.25m)`, convert to `MeasurementUnit.Tsp` (`tsp`).
    2. **`Tbsp` step-down (`qty < 1m`):**
       - Compute `tsp = qty * 3m`. If `IsCleanMultiple(tsp, 0.25m)` (e.g., `1/2 tbsp = 1.5 tsp`, `1/3 tbsp = 1 tsp`), convert to `MeasurementUnit.Tsp` (`tsp`).
    3. **`Tbsp` step-up (`qty >= 4m`, i.e. `>= 1/4 cup`):**
       - Compute `cups = qty / 16m`.
       - If `IsCleanCupAmount(cups)` (matches whole, `1/4`, `1/3`, `1/2`, `2/3`, or `3/4` cup within `0.01m`), convert to `MeasurementUnit.Cup` (`cups`) (or `MeasurementUnit.Qt` if `cups >= 4m` and `IsCleanMultiple(cups / 4m, 0.25m)`).
    4. **`Tsp` step-up (`qty >= 3m`):**
       - Compute `cups = qty / 48m`. If `qty >= 12m && IsCleanCupAmount(cups)`, convert to `MeasurementUnit.Cup` (`cups`).
       - Otherwise compute `tbsp = qty / 3m`. If `IsCleanMultiple(tbsp, 0.5m)` (whole or half tablespoons, e.g., `3 tsp = 1 tbsp`, `4.5 tsp = 1 1/2 tbsp`, `6 tsp = 2 tbsp`), convert to `MeasurementUnit.Tbsp` (`tbsp`).
    5. **`Qt` step-down (`qty < 1m`):**
       - Compute `cups = qty * 4m`. If `cups < 4m && IsCleanCupAmount(cups)`, convert to `MeasurementUnit.Cup` (`cups`) (and if `cups < 0.25m`, cascade to `Tbsp`/`Tsp`).
  - **US Weight (`Oz = 1`, `Lb = 16`):**
    1. **`Oz` step-up (`qty >= 16m`):**
       - Compute `lbs = qty / 16m`. If `IsCleanMultiple(lbs, 0.25m)`, convert to `MeasurementUnit.Lb` (`lbs`).
    2. **`Lb` step-down (`qty < 0.25m`):**
       - Compute `oz = qty * 16m`. Convert to `MeasurementUnit.Oz` (`oz`).
  - **Metric (`Ml`/`L`, `G`/`Kg`):**
    - `L` with `qty < 0.1m` (`< 100 ml`) $\rightarrow$ `Ml` (`qty * 1000m`); `Ml` with `qty >= 1000m` $\rightarrow$ `L` (`qty / 1000m`).
    - `Kg` with `qty < 0.1m` (`< 100 g`) $\rightarrow$ `G` (`qty * 1000m`); `G` with `qty >= 1000m` $\rightarrow$ `Kg` (`qty / 1000m`).

---

## 3. API Endpoints

No API endpoint changes are required.

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Mirror Unit Simplification in TypeScript (`src/app/core/utils/culinary-math.ts`)

- Update `convertUnit(qty, maxQty, unit, targetSystem, scaleFactor = 1)` and `scaleAndConvert(...)` in `Client/src/app/core/utils/culinary-math.ts` with the exact same `simplifyIntraSystemUnit` logic as `Core/Recipes/CulinaryMath.cs`.
- Add comprehensive unit tests in `Client/src/app/core/utils/culinary-math.spec.ts` covering:
  - `1 tbsp` at `4x` (`Original` and `UsCustomary`) $\rightarrow$ `1/4 cup`.
  - `1/4 cup` at `0.5x` (`Original` and `UsCustomary`) $\rightarrow$ `2 tbsp`.
  - `1 tbsp` at `0.5x` $\rightarrow$ `1 1/2 tsp`.
  - `2 tsp` at `3x` $\rightarrow$ `2 tbsp`.
  - `8 oz` at `2x` $\rightarrow$ `1 lb`.
  - `1/4 lb` at `0.5x` $\rightarrow$ `2 oz`.
  - `1 tbsp` at `1x` (`Original`) stays `1 tbsp` (author's unit preserved at `1x`).

### 4.2 Component Styling Fixes

1. **Sub-Recipe Ingredients (`src/app/features/recipes/recipe-detail/recipe-detail.component.scss` & `src/app/features/recipes/public-shared-recipe/public-shared-recipe.component.scss`):**
   - In `.ingredient-row`:
     ```scss
     .ingredient-row {
       display: flex;
       flex-wrap: wrap;
       align-items: center;
       justify-content: space-between;
       row-gap: 0.35rem;
     ```
   - Style `.sub-recipe-inline-panel` as a full-width child below the main row:
     ```scss
     .sub-recipe-inline-panel {
       flex: 0 0 100%;
       width: 100%;
       box-sizing: border-box;
       margin-top: 0.35rem;
       background: var(--mat-sys-surface-container-low);
       border: 1px solid var(--mat-sys-outline-variant);
       border-radius: 0.5rem !important;

       .sub-recipe-content {
         display: flex;
         flex-direction: column;
         gap: 0.75rem;
         padding-top: 0.5rem;
       }

       .sub-recipe-link-bar {
         display: flex;
         align-items: center;
         justify-content: space-between;
         flex-wrap: wrap;
         gap: 0.5rem;
       }

       .sub-ingredients-list ul {
         list-style: disc;
         margin: 0.35rem 0 0 1.25rem;
         padding: 0;
         display: flex;
         flex-direction: column;
         gap: 0.25rem;
       }
     }
     ```
   - Un-nest `.unresolved-recipe-reference` and `.unresolved-recipe-label` so they are direct children of `.ingredients-list` / `.ingredient-row` with `flex: 0 0 100%; width: 100%; box-sizing: border-box; margin: 0.25rem 0 0;`.

2. **Cookbook Collection Pill Mobile Overflow & Circular Hover Buttons (`src/app/features/recipes/recipe-list/recipe-list.component.scss`):**
   - In `.collection-pill-group`:
     ```scss
     .collection-pill-group {
       display: inline-flex;
       align-items: center;
       max-width: 100%;
       ...
       .collection-pill {
         flex: 1 1 auto;
         flex-shrink: 1;
         min-width: 0;
         white-space: normal;
         text-align: left;
         overflow-wrap: anywhere;
       }
     }
     ```
   - In `.collection-pill-action` (`lines 94–104`) and `.tag-pill-action` (`lines 359–369`):
     ```scss
     .collection-pill-action,
     .tag-pill-action {
       display: inline-flex;
       align-items: center;
       justify-content: center;
       width: 34px;
       height: 34px;
       aspect-ratio: 1;
       flex-shrink: 0;
       margin: 2px;
       padding: 0;
       border: none;
       border-radius: 50%;
     ```

3. **Responsive Hidden-Text Icon Button Margins:**
   - **`src/app/features/shell/app-shell/app-shell.component.scss`:**
     - On `.user-profile-btn` (at all viewport widths, since `.user-name` is `display: none`):
       ```scss
       .user-profile-btn {
         min-width: 40px;
         width: 40px;
         height: 40px;
         padding: 0;
         border-radius: 50%;
         display: inline-flex;
         align-items: center;
         justify-content: center;
         --mat-button-text-leading-icon-leading-space: 0;
         --mat-button-text-leading-icon-trailing-space: 0;

         .mat-icon.user-avatar-icon {
           margin: 0;
         }
       }
       ```
   - **`src/app/features/recipes/cook-mode/cook-mode.component.scss` (`@media (max-width: 480px)`):**
     - On `.exit-cook-btn`: set `--mat-button-text-leading-icon-leading-space: 0; --mat-button-text-leading-icon-trailing-space: 0; min-width: 40px; padding: 0 8px;` and `.mat-icon { margin: 0; }`.
     - On `.narration-pill-btn` and `.voice-control-pill-btn`: add `justify-content: center; padding: 0 0.5rem;`.
   - **`src/app/features/ingestion/source-viewer/source-viewer.component.scss` (`@media (max-width: 600px)`):**
     - Add `mat-button-toggle mat-icon { margin: 0; }` and `.cover-action button { --mat-button-filled-leading-icon-leading-space: 0; --mat-button-filled-leading-icon-trailing-space: 0; --mat-button-outlined-leading-icon-leading-space: 0; --mat-button-outlined-leading-icon-trailing-space: 0; min-width: 40px; padding: 0 10px; .mat-icon { margin: 0; } }`.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/`)

Add/update E2E test methods in `RecipeDetailE2ETests.cs` / `RecipeListE2ETests.cs`:
- `[TestMethod] RecipeDetail_ScalingSimplifiesUnits_QuadrupleTbspToQuarterCupAndHalfQuarterCupToTwoTbsp()`:
  - Seed a recipe with `1 tbsp olive oil` and `1/4 cup lemon juice`.
  - At `4x` scale, verify `1 tbsp` displays as `1/4 cup`.
  - At `1/2x` scale (`0.5x`), verify `1/4 cup` displays as `2 tbsp`.
- `[TestMethod] RecipeDetail_SubRecipeIngredient_RendersFullWidthExpansionPanelBelowIngredientRow()`:
  - Seed a sub-recipe and a parent recipe linking to it (like *Blueberry Pie*); verify `.sub-recipe-inline-panel` renders below the checkbox on its own row (`panelBox.Y >= checkboxBox.Y + checkboxBox.Height - 4`) and expands cleanly.
- `[TestMethod] RecipeList_LongCollectionPillAndActionButtons_MobileViewport360px_NoOverflowAndCircularButtons()`:
  - Seed a collection named `"The Complete America's Test Kitchen TV Show Cookbook"`.
  - At `360x640` viewport, verify `.collection-pill-action` buttons remain inside `.collection-pill-group` (`actionBox.X + actionBox.Width <= groupBox.X + groupBox.Width + 1`) and have equal width and height (`Math.Abs(actionBox.Width - actionBox.Height) <= 1`).
  - Verify `[aria-label="User profile and account options"]` has centered icon and compact width (`<= 44px`).

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend `CulinaryMath.cs` & Unit Tests**
  - Implement intra-system unit simplification in `Core/Recipes/CulinaryMath.cs`.
  - Add unit tests in `Tests/Core.UnitTests/CulinaryMathTests.cs` covering `1 tbsp * 4 -> 1/4 cup`, `1/4 cup * 0.5 -> 2 tbsp`, `1 tbsp * 0.5 -> 1 1/2 tsp`, `8 oz * 2 -> 1 lb`, `1/4 lb * 0.5 -> 2 oz`, and `1x` preservation.
  - Run Core unit tests:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    ```

- [x] **Task 2: Frontend `culinary-math.ts` & UI/SCSS Fixes**
  - Mirror unit simplification in `Client/src/app/core/utils/culinary-math.ts` and add tests in `culinary-math.spec.ts`.
  - Update `recipe-detail.component.scss` and `public-shared-recipe.component.scss` for sub-recipe panel layout and unresolved recipe badge styling.
  - Update `recipe-list.component.scss` for `.collection-pill-group` mobile wrapping and circular `.collection-pill-action` / `.tag-pill-action` buttons.
  - Update `app-shell.component.scss`, `cook-mode.component.scss`, and `source-viewer.component.scss` to remove hidden-text icon margins.
  - Run frontend lint, build, and unit tests in `Client/`:
    ```powershell
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [x] **Task 3: Playwright E2E Verification & Final Formatting**
  - Add E2E tests in `Tests/Client.E2ETests/` and run:
    ```powershell
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format all backend and frontend files and run full solution test suite:
    ```powershell
    dotnet csharpier format .
    dotnet test App.slnx
    ```
  - Mark this plan's status as `[x] Completed`.
