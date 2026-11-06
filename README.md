# LocalRoast

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Publish Docker](https://github.com/TylerSmart/LocalRoast/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/TylerSmart/LocalRoast/actions/workflows/docker-publish.yml)

**LocalRoast** is an open-source, self-hosted **household recipe vault, local-AI multimodal ingestion engine, meal planning calendar, and real-time shopping list app** built for **TrueNAS Scale** and homelab environments.

Ingest recipes from web URLs, cookbook camera photos, PDFs, or pasted text via a local LLM (`Ollama` or OpenAI-compatible `/v1` host) with a Side-by-Side Review Inbox; connect external AI assistants over an embedded **MCP server** (`/api/mcp`); plan real-world weeks of meals (including leftovers and an "On-Deck" flexible queue); co-shop in real time with aisle-ordered lists; and cook hands-free in **Cook Mode**.

- **[Product Requirements Document (PRD)](docs/prd.md)** — Full domain model, ingestion & MCP architecture, user workflows, and milestone roadmap.
- **[TrueNAS Scale Deployment Guide](docs/deployment/truenas-scale.md)** — Dataset setup, Docker Compose, and Nginx Proxy Manager configuration.
- **[Contributing & Local Development Guide](docs/contributing.md)** — Local Docker SQL Server setup, EF migrations, and testing commands.

---

## Core Capabilities

- **Multimodal Local-LLM Ingestion & Review Inbox:** Queue Web URLs, raw text, multi-page mobile camera cookbook scans, and PDFs into SQL Server (resilient even when your GPU/Ollama host is asleep), extract structured ingredients/steps/timers/tags, download hero photos locally to prevent link rot, and verify in a Side-by-Side Review screen.
- **Embedded MCP Server (`/api/mcp`):** Authenticate Claude Desktop, Antigravity, Open WebUI, or voice assistants via Personal Access Tokens (`lr_pat_...`) to search your cookbook, ingest/save recipes, plan weekly meals, and update shopping lists.
- **Canonical Food Catalog & Culinary Math:** Structured quantities, ranges, units, section headers, and linked Sub-Recipes backed by a shared `FoodItem` dictionary (default grocery aisles, aliases, and Pantry Staples) for accurate scaling and US/Metric conversion.
- **Flexible Household Meal Planning:** Multi-dish `Breakfast`, `Lunch`, `Dinner`, and `Snack / Prep` calendar slots, custom serving overrides, `Leftovers` tracking (excluded from shopping math), freeform notes, an **"On-Deck"** weekly tray, and reusable Week Templates.
- **Real-Time Smart Shopping Lists:** Multiple named lists (*Weekly*, *Costco*, *Trader Joe's*) with unit consolidation, recipe provenance badges, auto-separated Pantry Staples, custom Store Aisle walking layouts, and live **SignalR** multi-phone sync.
- **Hands-Free Cook Mode & Cook Logs:** Screen wake-lock (`navigator.wakeLock`), large step-by-step cards with inline scaled ingredient callouts, floating multi-timers, *"I Made This!"* Cook Logs (ratings, tweak notes, photos), **"Use Up Ingredients"** fridge matching, and revocable `/share/<token>` links.
- **Responsive Workspace:** Wide-screen navigation, a two-row tablet header, and safe-area-aware phone navigation. Forms and dialogs reflow on small screens, month planning becomes a phone-friendly agenda, and light/dark themes share accessible status colors.

---

## Tech Stack

| Layer | Technology |
|---|---|
| **Frontend** | [Angular 22](https://angular.dev/), TypeScript, Angular Material 3, RxJS, Signals |
| **Backend** | [ASP.NET Core 10](https://dotnet.microsoft.com/), SignalR, Model Context Protocol (`/api/mcp`), [Scalar](https://scalar.com/) OpenAPI (`/api/scalar`), Serilog |
| **AI / Ingestion** | Local-first `Ollama` & OpenAI-compatible (`/v1`) LLM/Vision integration |
| **Data & ORM** | [Microsoft SQL Server 2022](https://www.microsoft.com/sql-server), Entity Framework Core 10 |
| **Testing** | MSTest (`Microsoft.Testing.Platform`) + `WebApplicationFactory` (InMemory DB), Vitest, Playwright (E2E) |
| **Packaging** | Docker, Docker Compose, Multi-Arch Container Images via GitHub Actions |

---

## Quick Start (Docker Compose / TrueNAS Scale)

```yaml
name: local-roast-prod

services:
  api:
    image: ghcr.io/tylersmart/localroast:latest
    container_name: local-roast-api
    restart: unless-stopped
    ports:
      - "62781:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_HTTP_PORTS: 8080
      ConnectionStrings__DefaultConnection: "Server=sqlserver;Database=LocalRoast;User Id=sa;Password=YourStrongPassword123!_MustIncludeCaps;TrustServerCertificate=True;"
      Authentication__Jwt__Secret: "ReplaceWithAStrongSecretKeyAtLeast32CharactersLong!"
      Storage__UploadsPath: "/data/uploads"
      # Optional seed defaults for your LAN Ollama / OpenAI-compatible host (editable in UI):
      # Llm__Provider: "Ollama"
      # Llm__BaseUrl: "http://ollama.lan:11434"
    volumes:
      - /mnt/tank/localroast/uploads:/data/uploads
    depends_on:
      sqlserver:
        condition: service_healthy

  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: local-roast-sqlserver
    restart: unless-stopped
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "YourStrongPassword123!_MustIncludeCaps"
      MSSQL_PID: "Developer"
    volumes:
      - /mnt/tank/localroast/db:/var/opt/mssql
    healthcheck:
      test:
        [
          "CMD-SHELL",
          "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrongPassword123!_MustIncludeCaps' -C -Q 'SELECT 1' -b -o /dev/null",
        ]
      interval: 5s
      timeout: 5s
      retries: 24
      start_period: 20s
```

Run:

```bash
docker compose up -d
```

Open `http://localhost:62781` (or your reverse-proxied HTTPS domain) in your browser.

---

## Documentation

- 📋 **[Product Requirements Document (`docs/prd.md`)](docs/prd.md)**
- 🔄 **[Syncing Upstream Template Changes (`docs/template-sync.md`)](docs/template-sync.md)**
- 🐳 **[Production Docker Deployment Guide (`docs/deployment/docker-deployment.md`)](docs/deployment/docker-deployment.md)**
- 🚀 **[TrueNAS Scale Deployment Guide (`docs/deployment/truenas-scale.md`)](docs/deployment/truenas-scale.md)**
- 🔐 **[OIDC & SSO Authentication Guide (`docs/deployment/oidc-authentication.md`)](docs/deployment/oidc-authentication.md)**
- 🤖 **[Recommended Local LLM Model Settings (`docs/llm-model-settings.md`)](docs/llm-model-settings.md)**
- 🛠️ **[Contributing & Development Guide (`docs/contributing.md`)](docs/contributing.md)**

---

## License

This project is free and open-source software licensed under the **[MIT License](LICENSE)**.
