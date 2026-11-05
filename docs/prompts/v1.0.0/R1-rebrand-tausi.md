# Task R1: Rebrand to Tausi POS

Read `AGENTS.md` first. It has been updated: the product is now **Tausi POS**, published by **NexaGen Solutions** (nexagensolutions.com). Code names use the prefix `Tausi`.

This is a rename only. **Do not change behaviour, APIs, permission keys, database schema or tests' meaning.** Every test that passed before must pass after.

## Branch

- Start from the latest `main`: `git switch main && git pull && git switch -c chore/r1-rebrand-tausi`.
- If a task branch (e.g. `screen/04-admin-terminals`) is open and not yet merged, **do not touch it**. The owner will merge R1 first and rebase the other branch, or the reverse. Mention any likely conflicts in your report.
- Commit in small, reviewable steps (see order below). Push the branch and stop. **Never merge into `main` or create tags.**

## What to rename

### 1. Solution, projects and namespaces
- `server/Nexagen.sln` → `server/Tausi.sln`
- Every project `Nexagen.*` → `Tausi.*` (folders, `.csproj` file names, `RootNamespace`, `AssemblyName`, project references).
- All C# namespaces and `using` lines `Nexagen.` → `Tausi.`.
- `NexagenDbContext` → `TausiDbContext` (class, file, DI registration, design-time factory).
- Use `git mv` so history follows the files.

### 2. EF Core migrations
- **Do not edit or regenerate existing migrations' Up/Down bodies.** Only update their namespaces and the `[DbContext(typeof(TausiDbContext))]` attributes and the model snapshots' context type, for both the PostgreSQL and SQL Server migration projects.
- Confirm `dotnet ef migrations has-pending-model-changes` reports no changes for both providers. If it reports changes, stop and explain rather than adding a migration.

### 3. Database and configuration
- Dev database name `NexagenPOS` → `TausiPOS` in `appsettings.Development.json`, docker-compose, Testcontainers setup, scripts and docs.
- Production connection strings are owner-configured; do not hard-code a migration of data. Add a short note in `RUNNING.md`: "Existing dev databases named NexagenPOS: either rename the database or point the connection string at the old name."
- Env var `NEXAGEN_ADMIN_PASSWORD` → `TAUSI_ADMIN_PASSWORD`. Keep reading the old name as a fallback, log a one-time warning when it is used, and add a test for the fallback.
- Any other `NEXAGEN_*` env vars or config sections: same pattern (new name, old as fallback).
- Windows service name (if already defined) → `TausiPOSServer`, display name "Tausi POS Server", description "Tausi POS server by NexaGen Solutions".

### 4. Terminal app (Tauri)
- `tauri.conf.json`: `productName` "Tausi POS", `identifier` `com.nexagensolutions.tausipos`, main window title "Tausi POS".
- Bundle settings: `publisher` "NexaGen Solutions", `copyright` "© NexaGen Solutions", short/long description mentioning Tausi POS.
- Rust crate/package name → `tausi-terminal` (update `Cargo.toml`, `main.rs`/`lib.rs` references and the lib name).
- The identifier change means a fresh app-data folder. The store holds the device token, so a terminal would need re-pairing after updating. Add a one-time migration on startup: if the new store is empty and the old app-data folder (`com.nexagen.pos` or whatever the old identifier was; check the git history) has a store, copy it across. Add a note in `DECISIONS.md`.
- Updater endpoint/paths, if any, renamed accordingly.

### 5. UI (terminal and admin site)
- Every user-visible "Nexagen POS" → "Tausi POS"; admin sidebar sub-label "Tausi POS admin".
- `<title>` tags, `package.json` `name` fields (`tausi-ui` etc.), favicon alt text, About/footer: "Tausi POS · by NexaGen Solutions · version [x]".
- The business name and logo on the login screen and receipts stay as configured in settings (e.g. Mau's Village). Do not put "Tausi" on customer receipts except an optional small footer line "Powered by Tausi POS", **off by default**, behind a new setting `print.poweredByFooter` (bool, default false) in Settings → Printing.

### 6. CI, docs and repo
- GitHub Actions workflow names, job names and artifact names (`tausi-server-win-x64`, `TausiPOS-Setup.exe`, etc.).
- `README.md`, `RUNNING.md`, `DECISIONS.md`, `docs/` (except `docs/wireframes/*.pdf`, which the owner will replace), and `prompts/`: change product names in current docs. **Leave old prompt files as they are** (they are history), but add one line at the top of `prompts/README.md` (create it if missing): "Prompts before R1 use the old name Nexagen POS; it is now Tausi POS."
- `LICENSE` / copyright headers: "NexaGen Solutions".
- Add a `DECISIONS.md` entry: "Renamed product to Tausi POS (publisher NexaGen Solutions). Code prefix Tausi. Old env vars accepted as fallback; terminal store migrated from the old app identifier."

## Order of commits
1. Server rename (solution, projects, namespaces, DbContext, migrations' references). Build + tests green.
2. Config and env vars with fallbacks + tests.
3. UI strings and package names. `npm run build`, Vitest green.
4. Tauri config, crate name, store migration. `cargo check` and `npm run tauri build` (on CI) green.
5. CI, docs, DECISIONS.md, the powered-by setting.

## Done when
- `git grep -i nexagen` returns only: "NexaGen Solutions"/`nexagensolutions` (publisher, identifier), the env-var fallbacks and their test, the store-migration code, old prompts and `DECISIONS.md` history. List any other remaining hits in your report with the reason.
- `dotnet build`, `dotnet test` (both providers via Testcontainers), `npm test`, `npm run build`, `cargo check` and CI all pass.
- The terminal app launches as "Tausi POS", keeps its pairing after upgrade (tested by simulating the old store), and the admin site shows "Tausi POS admin".
- Your report lists: files/folders renamed, anything you were unsure about, and expected conflicts with any open task branch.
