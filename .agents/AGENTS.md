# Agent Context & Quick Reference

> **Purpose:** Immediate architectural, domain, and tooling context for AI coding agents working in this repository so you do not need to scan the codebase to understand conventions.

## 1. Tech Stack & Runtime Ports

- **Backend:** .NET 10 (`net10.0`), ASP.NET Core Web API, Entity Framework Core 10 (SQL Server in dev/prod, EF InMemory in integration tests), Serilog, Scalar OpenAPI (`/api/scalar`).
- **Frontend:** Angular 22 (`Client/`), Angular Material 3 + Material Symbols Outlined (`material-symbols-outlined`), TypeScript (strict), Signals, SCSS.
- **Ports:**
  - Local SQL Server (Docker): `localhost,14333` (started via `.\scripts\ensure-local-infra.ps1`)
  - Local .NET API: `http://localhost:5041` / `https://localhost:7127`
  - Local Angular Dev Server: `http://localhost:4200` (proxies `/api` to `http://localhost:5041`)

---

## 2. Repository Map

| Path | Responsibility |
| :--- | :--- |
| `docs/prd.md` | **Source of Truth** for product requirements, domain entities, and roadmap milestones (in downstream forks). |
| `Core/` (`App.Core`) | Domain interfaces (`I*Service`, `IUserContext`), immutable `record` DTOs/Requests, and `DomainExceptions`. **Zero EF Core or HTTP dependencies.** |
| `Data/` (`App.Data`) | `AppDbContext`, EF entities (`Entities/`), fluent configs (`Configurations/`), `AuditSaveChangesInterceptor`, migrations (`Migrations/`), and data-backed services (`Services/`). |
| `Infrastructure/` (`App.Infrastructure`) | Auth token generation (`JwtTokenService`), password hashing, external OIDC (`OidcService`), and external integrations. |
| `API/` (`App.API`) | Thin `ControllerBase` HTTP adapters (`Controllers/`), auth handlers (`DualBearerAuthenticationHandler`), OpenAPI/Scalar setup, and SPA static hosting. |
| `Client/` (`App.Client`) | Angular 22 SPA (`src/app/app.branding.ts` for app branding, `src/app/core/` for services/models/guards/interceptors, `src/app/features/` for lazy-loaded views & dialogs). |
| `Tests/` | `Tests/Core.UnitTests`, `Tests/Infrastructure.UnitTests`, `Tests/API.IntegrationTests`, `Tests/Client.E2ETests`. |

---

## 3. Detailed Rule Files (`.agents/rules/`)

- [Architecture & Layer Rules](rules/architecture.md): Vertical slice checklist, strict lean-controller boundaries, and template sync discipline.
- [Domain Rules](rules/domain.md): Application-specific domain entities and business invariants (customized per fork).
- [Backend (.NET 10 & EF Core) Rules](rules/backend-dotnet.md): Controller routing, `IAuditable`, DTOs, DI registration, and `ProblemDetails` exception mapping.
- [Frontend (Angular 22) Rules](rules/frontend-angular.md): Strict external `.ts`/`.html`/`.scss` component files, `@Service()`, signals, Material 3 tokens, and responsive design.
- [Commands, Testing & Formatting Rules](rules/commands-and-testing.md): Exact CLI syntax for `.NET 10` `Microsoft.Testing.Platform`, EF migrations, CSharpier, ESLint, Prettier, and **mandatory Playwright E2E test verification on all UI/client-facing changes**.

---

## 4. Two-Phase Development Skills (`.agents/skills/`)

- **`/plan-phase`** ([`.agents/skills/plan-phase/SKILL.md`](skills/plan-phase/SKILL.md)): Run with a **high-thinking model** to design the next vertical slice/phase from `docs/prd.md` and commit a zero-ambiguity blueprint in `docs/plans/phase-<NN>-<slug>.md`.
- **`/execute-phase`** ([`.agents/skills/execute-phase/SKILL.md`](skills/execute-phase/SKILL.md)): Run with a **fast implementation model** to execute the latest blueprint in `docs/plans/` step-by-step (including EF Core migrations and the mandatory test-and-fix loop).
