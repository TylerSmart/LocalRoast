# Commands, Testing & Formatting Rules

> **Token-Saving Notice:** Use the exact commands below. Do not guess CLI flags.

## 1. Mandatory Test-and-Fix Loop & Verification Honesty

On **every** code implementation, you MUST:
1. **Write/Update Tests** across all affected layers:
   - **Backend Unit Tests:** `Tests/Core.UnitTests` & `Tests/Infrastructure.UnitTests` for domain/service/infrastructure logic.
   - **API Integration Tests:** `Tests/API.IntegrationTests` (using `AppWebApplicationFactory`) for every new or modified controller endpoint, auth rule, or permission boundary.
   - **Frontend Unit Tests:** `.spec.ts` files in `Client/` for new/updated services, components, guards, and interceptors.
   - **Playwright E2E Tests:** `Tests/Client.E2ETests` for end-to-end user-facing feature flows:
     - Tests inherit from `E2ETestBase` and connect to `AssemblyInitializer.ServerUrl` (a background API child process serving `Client/dist/App.Client/browser` with in-memory DB).
     - Configured with `[assembly: DoNotParallelize]` for deterministic, reliable browser execution.
     - Every `[TestMethod]` MUST include `[Timeout(DefaultTestTimeoutMs)]` so no E2E test can hang indefinitely.
     - When debugging a test from Visual Studio (`Debugger.IsAttached`), `AssemblyInitializer` automatically enables headed browser mode (`HEADED=1`) and `E2ETestBase` disables Playwright action/navigation timeouts so developers can step through C# breakpoints with a live browser window. Developers can also select `Tests/Client.E2ETests/playwright.headed.runsettings` for headed runs with `SlowMo`.
     - On failure, `E2ETestBase` automatically saves debug artifacts (`screenshot.png`, `video.webm`, `page.html`, `page-data.json`, `console.log`, `network-errors.log`, `trace.zip`, and `api-server.log`) to the git-ignored `Tests/Client.E2ETests/DebugArtifacts/` directory.
     - Every phase delivering or updating user-facing UI **MUST** implement dedicated E2E tests in `Tests/Client.E2ETests/<Feature>E2ETests.cs` validating:
       1. Core user journeys and authenticated CRUD flows.
       2. Interactive dialogs, filters, toggles, and calculations.
       3. Mobile responsive behavior at `360px` viewport width.
2. **Execute & Fix Until Green:**
   - Build the full solution and run all relevant linters and test suites—not only targeted single-file tests—after every implementation iteration.
   - Diagnose, fix, and re-run iteratively until **all** builds, linters, and tests pass cleanly.
   - **Verification Honesty:** If a required build, migration, or test suite cannot be run or fails due to an external blocker, report the blocker clearly and never claim verification succeeded.
   - **Clean Staging:** When committing, stage only files related to your task so pre-existing untracked or user-modified files in the working tree are preserved.

---

## 2. Building & Running Backend Tests (.NET 10 Microsoft.Testing.Platform)

This solution uses **.NET 10** and **MSTest.Sdk v4 (`Microsoft.Testing.Platform`)**.
- **IMPORTANT:** `dotnet test` in `.NET 10` with `Microsoft.Testing.Platform` requires `--project` when targeting a specific `.csproj` (passing a positional path without `--project` fails with exit code 5).
- **IDE Lock Tip:** If `App.API` is currently running in Visual Studio or `dotnet watch`, building `App.API` directly may fail due to file locks on `App.API.exe`. You can still build `Core`, `Data`, `Infrastructure`, and run unit tests at any time.

```powershell
# Build entire solution (from repo root)
dotnet build App.slnx

# Run unit tests (fast, no Docker required)
dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj

# Run API integration tests (uses EF Core InMemory DB via AppWebApplicationFactory — no Docker required!)
dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj

# Run Playwright E2E tests (build Client first: npm run build:production in Client/)
dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
```

---

## 3. Frontend Build, Lint & Unit Tests (`Client/`)

Run all `npm` commands with working directory set to `Client`:

```powershell
# Build Angular app (production verification)
npm run build:production

# Lint TypeScript & HTML templates
npm run lint
npm run lint:fix

# Format frontend files with Prettier
npm run format
npm run format:check

# Run Angular unit tests (single run, non-interactive)
npm test -- --watch=false
```

---

## 4. Mandatory Entity Framework Core Migrations Workflow

Whenever you add or modify any EF Core entity (`Data/Entities/`), configuration (`Data/Configurations/`), or `AppDbContext`, **you MUST create, apply, format, and commit the EF Core migration** in the same commit as the model change.

- **CRITICAL (`--project Data/App.Data.csproj` only):** `Data/` includes `AppDbContextFactory` (`IDesignTimeDbContextFactory<AppDbContext>`). Always run `dotnet ef` targeting **only** `--project Data/App.Data.csproj` (do **not** pass `--startup-project API`). This avoids rebuilding `App.API` and works even while `App.API` is running in watch mode or Visual Studio.

```powershell
# 1. Ensure local Docker SQL Server is running (if not already up)
.\scripts\ensure-local-infra.ps1

# 2. Generate the migration (from repo root)
dotnet ef migrations add <MigrationName> --project Data/App.Data.csproj

# 3. Apply the migration immediately to local SQL Server
dotnet ef database update --project Data/App.Data.csproj

# 4. Format the generated migration files with CSharpier
dotnet csharpier format Data/Migrations
```

---

## 5. Code Formatting (Pre-Commit Requirement)

Before finishing a task or committing changes, always format backend and frontend code:

```powershell
# Format C# code (from repo root)
dotnet csharpier format .

# Format & lint Angular code (from Client/)
npm run format
npm run lint:fix
```
