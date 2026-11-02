# LocalRoast Domain Overview & Invariants

## 1. Source of Truth & Deployment Ports

- **Product Requirements:** [`docs/prd.md`](../../docs/prd.md) is the authoritative specification for LocalRoast entities, user workflows, and milestone phases.
- **Container / TrueNAS Port:** `62781:8080` (`docker-compose` production mapping).
- **Persistent Media Path:** `/data/uploads` in production containers (`/mnt/tank/localroast/uploads` on TrueNAS Scale), storing downloaded cover photos, WebP thumbnails, cook log photos, and uploaded ingestion PDFs/images outside SQL Server.

---

## 2. Built-In Shared Slices

- **Authentication & OIDC (`Auth`):**
  - Local email/password registration & login (`PBKDF2-SHA256` password hashing) plus optional external OAuth2/OIDC sign-in (`DualBearerAuthenticationHandler`).
  - Unique normalized email (`NormalizedEmail`) and normalized username (`NormalizedUsername`) per `User`.
- **Account & Theme Preferences (`Account`):**
  - Authenticated users can update their username, email, and password, and persist `ThemeSettings` JSON preferences across sessions.

---

## 3. Core LocalRoast Domain Invariants (`docs/prd.md`)

- **Single Global Household Tenancy:**
  - All authenticated users on the instance automatically share one global household cookbook, `FoodItem` catalog, ingestion review inbox, meal plan calendar, and set of shopping lists—there is no multi-tenant workspace or household-ID partitioning.
  - Individual user attribution is tracked via `IAuditable` (`CreatedBy`, `ModifiedBy`) and explicit actor fields (e.g., `CookLog.CookedByUserId`, `PersonalAccessToken.UserId`).
- **Canonical `FoodItem` & Culinary Math:**
  - Structured `RecipeIngredient` rows link to a canonical `FoodItem` (`DefaultStoreAisleId`, `IsPantryStaple`, synonyms/aliases) while preserving `OriginalRawText` and recipe-specific `DisplayName` / `PreparationNote`.
  - Sub-recipe component links (`LinkedRecipeId`) must enforce a strict Directed Acyclic Graph (DAG)—rejecting self-links or circular recipe chains with `DomainValidationException` / `ConflictException`.
  - Nutritional tracking is deferred out of scope, so do not introduce macro/calorie UI unless `docs/prd.md` is updated.
- **Resilient Ingestion Queue & LLM Isolation:**
  - Submitting a URL, text, image, or PDF must enqueue a persistent `IngestionJob` in SQL Server immediately without blocking HTTP requests on LLM inference.
  - When the configured `Ollama` or OpenAI-compatible (`/v1`) host is unreachable or offline, the core web UI must remain 100% functional, 1-click AI helpers must gracefully disable/hide, and pending `IngestionJob`s must transition to `WaitingForLlmHost` without losing uploaded source files.
- **Meal Plan & Shopping List Math:**
  - `MealPlanEntry` rows marked `IsLeftovers = true` (or freeform text notes without a recipe) **must never** contribute ingredient quantities when generating shopping lists.
  - Shopping lists render as clean **flat checklists** (no custom Store Layouts or shopping-list aisle grouping; `StoreAisle` remains strictly on `FoodItem` for Food Catalog organization).
  - Ingredients linked to a `FoodItem` with `IsPantryStaple = true` must be separated from active shopping list items into the Pantry Staples verification bucket during shopping list generation.
- **Strict Public & MCP Access Boundaries:**
  - Public recipe share links (`/share/<token>`) are strictly read-only and scoped **solely** to the single shared `Recipe` while active/unrevoked—never exposing other recipes, user emails, meal plans, or shopping lists.
  - Embedded MCP requests (`/api/mcp`) authenticate via SHA-256-hashed `PersonalAccessToken`s (`lr_pat_...`) and execute domain services under the owning user's identity.
