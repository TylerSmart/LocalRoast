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

## Commit & Branch Guidelines

- **Branch Naming**: `feature/<short-description>`, `fix/<short-description>`, `docs/<short-description>`, `chore/<short-description>`
- **Commit Messages**: Follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `refactor:`, `chore:`).
