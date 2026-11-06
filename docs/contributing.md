# Contributing Guide

Thank you for your interest in contributing! Whether you are fixing bugs, proposing new features, improving documentation, or testing out the application, this guide covers our local development workflow and standards.

---

## Table of Contents

1. [Code of Conduct](#code-of-conduct)
2. [How Can I Contribute?](#how-can-i-contribute)
3. [Local Development Environment](#local-development-environment)
   - [Prerequisites](#prerequisites)
   - [Setting Up Local Infrastructure](#setting-up-local-infrastructure)
   - [Running the Backend (API)](#running-the-backend-api)
   - [Running the Frontend (Angular)](#running-the-frontend-angular)
   - [VS Code Watch & Hot Reload](#vs-code-watch--hot-reload)
4. [Database Migrations (EF Core)](#database-migrations-ef-core)
5. [Code Quality & Standards](#code-quality--standards)
   - [Formatting & Linting](#formatting--linting)
   - [Running Tests](#running-tests)
   - [Evaluating ATK Ingestion](#evaluating-atk-ingestion-on-a-local-model-server)
6. [Commit & Branch Guidelines](#commit--branch-guidelines)

---

## Code of Conduct

We are committed to providing a welcoming, inclusive, and harassment-free environment for all contributors. Please treat fellow contributors with respect, empathy, and professional courtesy.

---

## How Can I Contribute?

1. **Create a feature branch** off `main` (`git checkout -b feature/my-new-feature`).
2. Make your changes following the [Code Quality & Standards](#code-quality--standards) and agent rules in [`.agents/AGENTS.md`](../.agents/AGENTS.md).
3. Verify that all backend and frontend builds, linters, and test suites pass.
4. Commit your changes with clear [Conventional Commits](#commit--branch-guidelines) messages and open a Pull Request targeting `main`.

---

## Local Development Environment

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22 LTS or newer](https://nodejs.org/)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (with Linux container support)

### Setting Up Local Infrastructure

For local development, Microsoft SQL Server runs in Docker on port `14333` while the .NET API and Angular client run natively on your host machine.

Run the setup script from the repository root:

```powershell
.\scripts\ensure-local-infra.ps1
```

This starts the SQL Server container (defined in `docker-compose.yml`) and ensures the configured local database exists.

### Running the Backend (API)

```powershell
# Standard run
dotnet run --project API/App.API.csproj

# Hot-reload file watching
dotnet watch --project API/App.API.csproj run
```

The API listens on `http://localhost:5041` (and `https://localhost:7127` when using the `https` profile). Interactive Scalar API documentation is available at `http://localhost:5041/api/scalar` (OpenAPI JSON at `/api/openapi/v1.json`).

### Running the Frontend (Angular)

```powershell
cd Client
npm install
npm start
```

The Angular dev server starts at `http://localhost:4200/` and proxies `/api` requests to `http://localhost:5041`.

### VS Code Watch & Hot Reload

Open the repository root in VS Code and launch **`Full Stack (watch)`** from the Run and Debug panel (`.vscode/launch.json`), or run the `api: watch` and `client: start` tasks from `.vscode/tasks.json`. To step through C# breakpoints while `dotnet watch` is running, run **`Attach to API (.NET)`**.

---

## Database Migrations (EF Core)

Schema definitions live in `AppDbContext` (`Data/`). Because `Data/` includes `AppDbContextFactory`, always run `dotnet ef` targeting `--project Data/App.Data.csproj` directly (no `--startup-project` needed):

```powershell
dotnet tool restore
```

All commands below run from the repository root:

- **Add a new migration**:

  ```powershell
  dotnet ef migrations add <MigrationName> --project Data/App.Data.csproj
  dotnet csharpier format Data/Migrations
  ```

- **Apply pending migrations**:

  ```powershell
  dotnet ef database update --project Data/App.Data.csproj
  ```

- **List applied migrations**:

  ```powershell
  dotnet ef migrations list --project Data/App.Data.csproj
  ```

- **Revert the last migration**:

  ```powershell
  dotnet ef database update <PreviousMigrationName> --project Data/App.Data.csproj
  dotnet ef migrations remove --project Data/App.Data.csproj
  ```

- **Generate raw SQL script**:
  ```powershell
  dotnet ef migrations script --project Data/App.Data.csproj --output schema.sql
  ```

Always commit generated files in `Data/Migrations/` in the same commit as the entity/configuration changes.

---

## Code Quality & Standards

### Formatting & Linting

- **Backend (.NET)** — Formatted using [CSharpier](https://csharpier.com/):
  ```powershell
  dotnet tool restore
  dotnet csharpier format .
  ```
- **Frontend (Angular)** — Formatted and linted using Prettier and ESLint (note: all Angular components must use external `.html` and `.scss` files, never inline templates/styles):
  ```powershell
  cd Client
  npm run lint:fix
  npm run format
  ```

### Running Tests

`.NET 10` uses `Microsoft.Testing.Platform`, which requires `--project` when running a specific test `.csproj`:

- **Backend Build & Tests** (from repo root):

  ```powershell
  dotnet build App.slnx
  dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
  dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
  dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
  dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
  ```

- **Frontend Build & Unit Tests** (from `Client/`):
  ```powershell
  npm run lint
  npm run build:production
  npm test -- --watch=false
  ```

---

- **Visual E2E Debugging & Failure Artifacts**: See the [Playwright E2E Testing & Debugging Guide](e2e-testing-and-debugging.md) for instructions on stepping through E2E tests with a live browser window in Visual Studio, using `playwright.headed.runsettings`, launching the Playwright Inspector, and inspecting failure artifacts in `Tests/Client.E2ETests/DebugArtifacts/`.

---

## Responsive UI Verification

UI changes should preserve both light and dark themes and work without page-level horizontal scrolling. Navigation uses a single desktop row from 1440px, a two-row header at 769-1439px, and bottom navigation at 768px and below. Month planning uses an agenda at 600px and below.

The `ResponsiveUiE2ETests` suite exercises pages, populated forms, nested dialogs, AI results, media viewers, public shares, and cooking controls at phone, tablet, and desktop sizes. It checks rendered bounds, dialog footer visibility, form sizing, and drawer keyboard focus, including short-screen settings dialogs.

```powershell
npm run build:production --prefix Client
dotnet test --project Tests\Client.E2ETests\App.Client.E2ETests.csproj --filter "FullyQualifiedName~ResponsiveUiE2ETests|FullyQualifiedName~NavigationAndThemeE2ETests"
```

To inspect successful states as well as automatic failure artifacts, set `LOCALROAST_RESPONSIVE_SCREENSHOTS` to a local output directory before running the responsive suite. Screenshots supplement the geometry checks; review long labels, scrolling, touch controls, and light/dark contrast visually. Run the complete E2E suite before finishing broad UI changes.

### Evaluating ATK Ingestion on a Local Model Server

Use your own ATK EPUB with the existing importer. The evaluator reuses its parser and raw-text formatter, calls the **production** .NET extraction client and draft normalizer, and does not enqueue jobs, publish recipes, change LLM settings, or touch a database.

```powershell
# Parser-only smoke test (no network).
python scripts\import_atk_epub.py --dry-run --limit 1

# Serial live evaluation: one request at a time, all cases for one model before switching.
python scripts\evaluate_atk_ingestion.py --epub "C:\Books\ATK.epub" `
  --base-url http://localhost:11434 --thinking --timeout 600 `
  --output "C:\PrivateBenchmarks\atk-run-001"

# A smaller iteration, using case IDs from scripts\atk_ingestion_cases.json.
python scripts\evaluate_atk_ingestion.py --epub "C:\Books\ATK.epub" `
  --base-url http://localhost:11434 --models gemma4:e4b --thinking `
  --ids c001-s1 c006-s368 --output "C:\PrivateBenchmarks\atk-run-002"

# Re-score saved outputs without any inference; use the same case/model selection as the run.
python scripts\evaluate_atk_ingestion.py --epub "C:\Books\ATK.epub" `
  --score-only --output "C:\PrivateBenchmarks\atk-run-001"

python -m unittest discover -s scripts -p "test_evaluate_atk_ingestion.py"
```

- The manifest contains four tuning recipes, three held-out recipes and one multi-day stress recipe, with independently specified quantity, portion, preparation and timer assertions. Add new cookbook IDs and assertions there to extend coverage.
- Output directories contain copyrighted source text, exact requests/responses, normalized drafts, validation diagnostics, timings and source-code/manifest hashes. **Keep them private and out of git.** A fresh output directory is required for every live run.
- `summary.json` separates evaluator findings from production validation diagnostics. Exit code 1 means extraction/scoring failures were found, not that the evaluator passed. Zero findings is not a guarantee that every culinary detail is correct: manually compare ingredients, intermediate mixtures, batches, serving counts, sections, doneness cues and timers against the source.
- `--temperature`, `--top-p`, `--top-k` and `--context-window` mirror the **Sampling** and **Context window** fields in LLM Settings and go through the same production request code, so prefer them for these experiments (for example `--temperature 1 --top-p 0.95 --top-k 64 --context-window 32768` for `gemma4:12b` with thinking). Omitted sampling values behave like blank fields; the context window defaults to 16,384 tokens. They are recorded in `metadata.json` and every result.
- `--ollama-options '{"num_gpu":24,"seed":42}'` is an explicitly recorded experiment for options LocalRoast cannot express, not a change to application/server defaults. Values in it override the corresponding LocalRoast request options. `--no-build` is only appropriate when the evaluator and referenced projects are already current.
- See [Recommended Local LLM Model Settings](llm-model-settings.md) for measured per-model settings (`gemma4:e2b`, `gemma4:e4b`, `gemma4:12b`, `gemma4:26b-a4b`) and Ollama server configuration. The staged small-Gemma path may make up to three requests per recipe; keep the server limited to one concurrent request and one loaded model. The application never enables thinking or rewrites household settings on your behalf.
- The evaluator uses an explicit 16,384-token context unless `--context-window` is given. Application requests honor the household's configured context window and CPU-only setting for all models. See the model guide for measured context recommendations; thinking models can need 32,768 tokens on long recipes.
- The schema, default zero-temperature extraction and repetition penalty 1 address reproducibility/contract errors, not model capability limits. Neither model is guaranteed to handle every recipe perfectly; in particular audit separated eggs, partially used prepared mixtures, alternatives and repeated cooking batches. Source equivalents use `alternativeMeasurement`; explicit per-step portions use `ingredientAmounts`, while separate portion rows remain supported and must conserve source totals.
- A prepared-mixture portion that cannot be represented by a linked ingredient must retain its source amount and remain reviewable, rather than silently losing that amount to satisfy a prose check. Such drafts are not ready for reliable serving-multiplier use.
- Source methods that measure intermediate stock, broth, batter, dough, filling or sauce not listed as an ingredient receive a separate manual-review warning, even if the model erased that amount. These structural limitations are not sent through another LLM correction loop.

## Commit & Branch Guidelines

- **Branch Naming**: `feature/<short-description>`, `fix/<short-description>`, `docs/<short-description>`, `chore/<short-description>`
- **Commit Messages**: Follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `refactor:`, `chore:`).
