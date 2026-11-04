# Playwright E2E Testing & Debugging Guide

This guide covers how to run, visually step through, and debug end-to-end (E2E) Playwright tests in `Tests/Client.E2ETests/`.

---

## Table of Contents

1. [Architecture & Prerequisites](#1-architecture--prerequisites)
2. [Running E2E Tests from the CLI](#2-running-e2e-tests-from-the-cli)
3. [Visual Debugging Workflows (Browser & Breakpoints)](#3-visual-debugging-workflows-browser--breakpoints)
   - [Option A: VS Code & Visual Studio "Debug Test" (Zero-Config Breakpoints + Live Browser)](#option-a-vs-code--visual-studio-debug-test-zero-config-breakpoints--live-browser)
   - [Option B: VS Code Run & Debug Profiles & `.runsettings` Configuration](#option-b-vs-code-run--debug-profiles--runsettings-configuration)
   - [Option C: Visual Studio "Run" with Headed + Slow-Motion `.runsettings`](#option-c-visual-studio-run-with-headed--slow-motion-runsettings)
   - [Option D: Playwright Inspector (`PWDEBUG=1` & `Page.PauseAsync()`)](#option-d-playwright-inspector-pwdebug1--pagepauseasync)
   - [Option E: Time-Travel Debugging with Playwright Trace Viewer](#option-e-time-travel-debugging-with-playwright-trace-viewer)
4. [Automatic Failure Debug Artifacts](#4-automatic-failure-debug-artifacts)
5. [Timeouts & Authoring Guidelines](#5-timeouts--authoring-guidelines)

---

## 1. Architecture & Prerequisites

The E2E test suite (`Tests/Client.E2ETests/App.Client.E2ETests.csproj`) uses **MSTest v4 (`Microsoft.Testing.Platform`)** and **Microsoft.Playwright.MSTest**:

- **`PlaywrightServerFixture` & `AssemblyInitializer`:** Before the first test runs, the test assembly boots `App.API` as a background child process on a dynamic localhost port with `--use-inmemory-db` (no Docker or SQL Server required).
- **Static SPA Hosting:** `App.API` serves the compiled Angular application from `Client/dist/App.Client/browser`.

> **Important Prerequisite:** Whenever you change frontend code in `Client/`, rebuild the Angular production bundle before running E2E tests so the API serves your latest UI:
> ```powershell
> cd Client
> npm run build:production
> cd ..
> ```

---

## 2. Running E2E Tests from the CLI

By default, CLI test runs execute **headless** at full speed:

```powershell
# Run the entire E2E test suite (from repo root)
dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj

# Run a single test class or test method by name
dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj -- --filter "FullyQualifiedName~MealPlanE2ETests"
dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj -- --filter "FullyQualifiedName~FoodCatalog_CreateItem_TogglePantryStaple_AndSearchByAlias"

# Run a test from the CLI with a visible browser window
$env:HEADED="1"; dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj -- --filter "FullyQualifiedName~ClientSmokeTests"; Remove-Item Env:HEADED
```

---

## 3. Visual Debugging Workflows (Browser & Breakpoints)

> **Why doesn't the VS Code "Playwright Test for VSCode" extension show these tests?**
> The official `ms-playwright.playwright` VS Code extension only detects Node.js/TypeScript `@playwright/test` projects (`playwright.config.ts`). Because this repository's E2E tests are written in **C# / .NET (`Microsoft.Playwright.MSTest`)**, they are discovered and executed by the **C# / C# Dev Kit extension** in VS Code's Testing tab and by Visual Studio's Test Explorer.

---

### Option A: VS Code & Visual Studio "Debug Test" (Zero-Config Breakpoints + Live Browser)

Clicking **Debug Test** (instead of **Run Test**) works out-of-the-box in **both VS Code and Visual Studio**:

1. Set a breakpoint (`F9`) on any line inside a `[TestMethod]` in `Tests/Client.E2ETests/*.cs`.
2. Start debugging the test:
   - **In VS Code:** In the **Testing** sidebar tab (beaker icon), hover over the test and click the **Debug Test** button (the play icon with a bug), or click **`Debug Test`** in the CodeLens link directly above `[TestMethod]` in the editor.
   - **In Visual Studio:** In **Test Explorer**, right-click the test and click **Debug**.

**What happens automatically when `Debugger.IsAttached` is `true`:**
- `AssemblyInitializer.AssemblyInit` automatically sets `HEADED=1` before Playwright launches Chromium, so a **visible browser window** opens on your desktop.
- `E2ETestBase` (`IsInteractiveDebugSession`) automatically sets Playwright's action and navigation timeouts to `0` (disabled), and MSTest suspends the `[Timeout]` attribute while the debugger is attached—so the browser will never time out while you are paused at a breakpoint inspecting variables or the DOM.
- Each time you press **Step Over (`F10`)**, you will see the corresponding click, fill, or dialog action happen live in the Chromium window.

---

### Option B: VS Code Run & Debug Profiles & `.runsettings` Configuration

In addition to clicking **Debug Test** in the VS Code Testing tab, `.vscode/launch.json` and `.vscode/tasks.json` include dedicated E2E profiles:

1. **VS Code Run and Debug (`Ctrl+Shift+D`) Launch Profiles:**
   - **`Debug E2E Tests (Headed Browser)`:** Prompts for a test class/method filter (defaults to `ClientSmokeTests`), builds the E2E project, and launches the test runner under the debugger with `playwright.headed.runsettings` (`Headless = false`, `SlowMo = 250ms`).
   - **`Debug E2E Tests (Playwright Inspector)`:** Prompts for a test filter and launches with `PWDEBUG=1` so the **Playwright Inspector** GUI opens alongside Chromium.
2. **VS Code Task (`Terminal -> Run Task...`):**
   - **`e2e: run headed (slow-mo)`:** Runs the E2E suite in a visible browser window with `250ms` slow-motion via `playwright.headed.runsettings`.
3. **Making the normal "Run Test" button in the VS Code Testing tab open a browser:**
   If you also want the normal **Run Test** play button (without debugger attached) in VS Code's Testing tab to open a visible browser with slow-motion, add the following setting to `.vscode/settings.json`:
   ```json
   "dotnet.unitTests.runSettingsPath": "${workspaceFolder}/Tests/Client.E2ETests/playwright.headed.runsettings"
   ```

---

### Option C: Visual Studio "Run" with Headed + Slow-Motion `.runsettings`

If you want to click **Run** (without stopping at breakpoints) in Visual Studio and watch the test execute at a human-readable pace:

1. In Visual Studio, go to **Test -> Configure Run Settings -> Select Solution Wide runsettings File**.
2. Select `Tests/Client.E2ETests/playwright.headed.runsettings`.
3. Run any E2E test from Test Explorer (or from the CLI with `--settings Tests/Client.E2ETests/playwright.headed.runsettings`).

This configuration sets:
- `<Headless>false</Headless>` (`HEADED=1`) so the Chromium window is visible.
- `<SlowMo>250</SlowMo>` so Playwright pauses for `250ms` between every click, fill, and navigation action.

*(To return to fast headless execution, uncheck the `.runsettings` file under **Test -> Configure Run Settings**.)*

---

### Option D: Playwright Inspector (`PWDEBUG=1` & `Page.PauseAsync()`)

Playwright includes a built-in interactive GUI called the **Playwright Inspector** that opens alongside Chromium and lets you:
- Step over Playwright actions from inside the browser UI.
- Hover over or pick DOM elements on the page to see the exact Playwright locator for any element.
- View live actionability logs (e.g., whether an element is visible, enabled, stable, or obscured by an overlay).

You can launch it in any of three ways:

1. **VS Code Launch Profile:** Select **`Debug E2E Tests (Playwright Inspector)`** in the VS Code Run and Debug panel (`Ctrl+Shift+D`).
2. **CLI Environment Variable (`PWDEBUG=1`):**
   ```powershell
   $env:PWDEBUG="1"
   dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj -- --filter "FullyQualifiedName~ClientSmokeTests"
   Remove-Item Env:PWDEBUG
   ```
3. **In Code (`await Page.PauseAsync()`):**
   Temporarily add `await Page.PauseAsync();` at the point in your test where you want to inspect the browser, then debug or run the test in headed mode.

---

### Option E: Time-Travel Debugging with Playwright Trace Viewer

Even when tests run headless, you often do not need to re-run a failed test in headed mode to see what went wrong. Every failed E2E test automatically records a full Playwright trace (`trace.zip`).

Run the following command pointing to the saved `trace.zip`:

```powershell
npx playwright show-trace Tests/Client.E2ETests/DebugArtifacts/<RunId>/<TestFolder>/trace.zip
```

The **Playwright Trace Viewer** opens in your browser and provides:
- A visual filmstrip and timeline of the entire test run.
- **Before / Action / After** interactive DOM snapshots for every click and input (you can open Chrome DevTools directly on the recorded DOM snapshot!).
- Full browser console logs, page errors, and network request/response waterfalls synced to each step.

---

## 4. Automatic Failure Debug Artifacts

Whenever an E2E test fails, times out, or is aborted, `E2ETestBase.E2ETestCleanupAsync` automatically writes a diagnostic bundle to the git-ignored folder:

```text
Tests/Client.E2ETests/DebugArtifacts/<RunId>/<ClassName>_<TestName>_<Timestamp>/
```

Each failure folder contains:

| File | Description |
| :--- | :--- |
| `screenshot.png` | Full-page PNG screenshot captured at the moment of failure (plus `<label>-screenshot.png` for any secondary browser contexts). |
| `video.webm` | `1280x720` video recording of the entire browser session from test start to failure. (Passing tests automatically clean up their temporary videos.) |
| `page.html` | Complete serialized HTML DOM (`page.ContentAsync()`) at the moment of failure. |
| `page-data.json` | JSON snapshot containing test outcome, duration, current URL, document title, viewport size, open `.cdk-overlay-pane` / dialog / snackbar text, `localStorage`, and `sessionStorage`. |
| `console.log` | Timestamped browser `console.*` messages and uncaught `pageerror` stack traces. |
| `network-errors.log` | Timestamped network request failures (`requestfailed`) and HTTP `>= 400` responses. |
| `trace.zip` | Interactive Playwright trace archive (viewable via `npx playwright show-trace`). |
| `api-server.log` | Standard output and standard error logs emitted by the backend `App.API` process during the test window. |

The exact path to the saved folder is printed in the test output (`[E2E DebugArtifacts] Saved failure artifacts to: ...`) and attached to the MSTest `TestContext` result files.

---

## 5. Timeouts & Authoring Guidelines

To guarantee that no E2E test can hang indefinitely in CI or local headless runs:

1. **Mandatory `[Timeout(DefaultTestTimeoutMs)]` Attribute:**
   Every `[TestMethod]` in `Tests/Client.E2ETests/` **must** include `[Timeout(DefaultTestTimeoutMs)]` (`90,000ms` / 90 seconds). This requirement is enforced automatically by reflection in `ClientSmokeTests.FailedTestArtifactCaptureAndTimeoutConfig_SavesVideoScreenshotDomConsoleNetworkTraceAndApiLogs`.
2. **Default Playwright Timeouts (Headless Mode):**
   - **Action Timeout (`DefaultActionTimeoutMs`):** `15,000ms` (15s) for clicks, fills, and locator actions.
   - **Navigation Timeout (`DefaultNavigationTimeoutMs`):** `30,000ms` (30s) for page navigations.
   - **Expect Assertion Timeout (`DefaultExpectTimeoutMs`):** `10,000ms` (10s) for `Expect(...)` assertions.
3. **Multi-User / Secondary Browser Contexts:**
   When a test needs a second isolated browser session (e.g., testing public share links as an unauthenticated visitor or real-time SignalR co-shopping between two users), always call `await CreateIsolatedPageAsync("label")` on `E2ETestBase` instead of `Browser.NewContextAsync()` directly so the secondary session inherits timeouts, tracing, video recording, and failure artifact capture.
