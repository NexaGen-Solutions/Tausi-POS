# AGENTS.md — Tausi POS

**Tausi POS** is a product of **NexaGen Solutions** (nexagensolutions.com). Use "Tausi POS" for the product everywhere a user can see it, and "NexaGen Solutions" as the publisher and company (installers, About screen, licence, copyright). Code names use the prefix `Tausi`.

Read this file before every task. It holds the rules that apply to **every** change in this repo. Task prompts describe *what* to build; this file describes *how*.

## What Tausi POS is

A touch POS **product** for bars and restaurants. Each business runs its own install:

- **Tausi Server**: a .NET service on the business's own server PC (Windows or Linux). It holds all data, business rules and printing.
- **Tausi Terminal**: a full-screen Tauri app on each counter PC (Windows 10/11) that shows the POS screens.
- **Admin site**: the same web UI, opened in a browser on the LAN, for back-office work.

Tausi POS is sold to many bars and restaurants. **Never hard-code a customer's name**, staff names or menu in code, UI text, docs or tests: use `[BUSINESS NAME]`-style placeholders, or the generic demo business **"Demo Bar"** in seed data and examples. The business name, logo, address, VAT rate and receipt footer come from `BusinessSettings`.

The design doc is in `docs/design.md` and the screen wireframes are in `docs/wireframes/`. When a task names a screen, match its wireframe's layout and behaviour.

## Stack (do not change without asking)

| Part | Technology |
| --- | --- |
| Server | .NET 10 (LTS), ASP.NET Core minimal APIs, SignalR, EF Core 10 |
| Database | PostgreSQL (default, Linux and dev) **and** SQL Server Express (Windows customers), using the same EF Core model and migrations per provider |
| Web UI | React + TypeScript + Vite, React Router, TanStack Query; no UI kit, use our own components in `web/src/shared/ui` |
| Terminal | Tauri v2, wrapping the web build; plugins: autostart, store, updater |
| Tests | xUnit + FluentAssertions (server), Testcontainers PostgreSQL (integration), Vitest + Testing Library (web), Playwright (end-to-end, later) |
| CI | GitHub Actions: build + test on Ubuntu; build the Tauri installer on `windows-latest` |

Development happens on **Ubuntu**. Everything except the Windows installer must build and run there.

## Repo layout

```
server/
  Tausi.sln
  src/Tausi.Domain/          entities, value objects, business rules (no EF, no ASP.NET)
  src/Tausi.Application/     use cases (one class per action), permission checks, DTOs
  src/Tausi.Infrastructure/  EF Core DbContext + migrations, printing transports, ledger posting, file storage
  src/Tausi.Api/             HTTP endpoints, SignalR hubs, auth, startup, static web files
  tests/Tausi.Tests/         unit tests (Domain + Application)
  tests/Tausi.IntegrationTests/  API + database tests (Testcontainers)
web/
  src/shared/   api client, auth/session, ui components, design tokens, formatting (money, dates)
  src/pos/      terminal screens
  src/admin/    admin site screens
terminal/       Tauri v2 app (src-tauri/)
docs/           design.md, wireframes/, DECISIONS.md, RUNNING.md
.github/workflows/
```

## Non-negotiable business rules

1. **The server decides.** Every permission is checked on the server, in the Application layer, before any change. Hiding a button in the UI is only a convenience.
2. **Permissions are data, not code.** Use `RolePermissions` (role × permission key → `Allowed` | `NeedsApproval` | `NotAllowed`). Check with `IPermissionService.Require(user, "permission.key")`. A `NeedsApproval` result requires an approval token from a user whose role has `Allowed`. Never write `if (role == "Supervisor")`.
3. **Every order line records who added it and on which terminal**, taken from the authenticated session, never from the request body. These fields are required in the database.
4. **Everything important is audited** via `IAuditLog.Write(...)`: who, what, when, terminal, old and new values. That covers logins, voids, gifts, reprints, price changes, permission changes, stock adjustments, cash-ups and settings changes.
5. **Money:**
    - Use `decimal`, never `double`. Currency comes from settings (KES by default).
    - Menu prices **include VAT**. For each line, VAT = gross × rate ÷ (100 + rate), rounded half away from zero to 2 decimals, and the line is stored as gross, net and VAT. Ticket totals are sums of lines.
    - Displayed and printed totals are rounded per the rounding setting (nearest shilling by default). Store any rounding difference as its own line.
6. **Ledger.** Every sale, payment, delivery, expense, stock change and cash-up posts **balanced** journal lines through `ILedgerPostingService`. No other code writes journal entries. Debits must equal credits; add a test whenever you add a posting.
7. **Idempotency.** Every write from the UI sends an `Idempotency-Key` header (a UUID). The server stores keys for 24 hours and returns the original result for repeats, so a retry never creates a duplicate order or payment.
8. **Concurrency.** Ticket operations (print, void, merge, settle) lock the ticket row. Use optimistic concurrency (a row version) on all editable entities.
9. **Printing goes through the queue.** Code creates a `PrintJob`, and the `PrintDispatcher` sends it through an `IPrinterTransport`: `Network9100`, `WindowsShare` or `ViaTerminal`. Print failures never roll back the business action. They show as "Not printed · Retry".
10. **Print order:** in one-button mode, Print sends the **customer bill (all items) first**, then the **dispense slip (only items not yet printed)**.
11. **Only the cashier settles** (permission `ticket.settle`). Cash payments record `TakenBy` (the cashier) and `BroughtBy` (the waiter).
12. **Never block service on optional things.** ERPNext sync, licence checks and update checks run in the background and degrade gracefully.

## Permission keys (use these exact strings)

`order.add`, `order.cancel_unprinted`, `order.void`, `order.gift`, `order.discount`, `order.change_price`, `ticket.print`, `ticket.reprint`, `ticket.move`, `ticket.merge`, `ticket.settle`, `ticket.others`, `menu.edit`, `menu.edit_prices`, `stock.receive`, `stock.waste`, `stock.count`, `stock.adjust`, `cash.expense`, `cash.book`, `reports.own`, `reports.all`, `day.open_close`, `staff.manage`, `permissions.manage`, `devices.manage`, `settings.manage`, `erp.manage`.

## Live updates (SignalR)

The hub is `/hubs/pos`, and clients join the group `business`. Events are named `noun.verb`: `tables.changed`, `ticket.changed`, `ticket.lock`, `terminal.status`, `approval.requested`, `approval.resolved`, `print.failed`, `printer.status`, `menu.changed`. Payloads are small. Clients refetch the details through the API.

## UI rules (match the wireframes)

- **Fonts:** IBM Plex Sans; IBM Plex Mono for money, quantities, PINs and codes. Self-host the font files, since there's no internet dependency at runtime.
- **Colours** (design tokens in `web/src/shared/ui/tokens.css`):
    - **Brand (from the Tausi POS logo):** teal `#0E6670`, deep teal `#0A4A52`, cream `#F6F1E4`, gold `#C9A227`
    - Ground `#F4F4F2` (admin and POS work screens), cream `#F6F1E4` (PIN screen, setup wizard, splash), panel `#FFFFFF`, border `#C9C9C4`
    - Text `#1C1C1C`, secondary text `#3D3D3A` / `#5C5C58`
    - Top bar and dark panels: deep teal `#0A4A52` with cream or white text
    - **Primary action: teal `#0E6670`** with white text (pressed `#0A4A52`)
    - Gold `#C9A227` for small accents only (active tab underline, focus ring, badges), never for text on light grounds
    - Selected / occupied blue `#1F5FA8`, tint `#E6EEF8`
    - **Warning / needs attention: orange `#B34A0C`**, tint `#FBE7DA`, text `#8A3708` (idle tables, "Not printed", failed, differences, Void button). Orange is never a primary button colour.
- **Brand assets** live in `web/src/shared/brand/` and `brand/` (logo, mark, icons). The Tausi logo shows only on Tausi's own surfaces (splash, pairing, setup wizard, installer, About line, admin footer). The **customer's** name and logo stay on the POS screens, bills and receipts. "Powered by Tausi POS" on receipts is text only, behind `print.poweredByFooter` (off by default).
- **Terminal screens** are designed for 1280×800 touch. Touch targets are at least 44 px; primary buttons at least 56 px. No hover-only actions.
- **Admin pages** use a left sidebar nav, must work at 1280 wide and down to tablet width, and wide tables scroll inside their box.
- Use real `<button>`, `<a>`, and `<input>` with `<label>`, so screens work with a keyboard too.
- No emoji, no gradients, no drop shadows.
- Text strings live in `web/src/shared/i18n/en.json` (English first, structured for translation later).

## How to work

- **Do only the task in the prompt.** If you notice something else needed, write it in `docs/DECISIONS.md` under "Follow-ups" instead of building it.
- **When a rule above is unclear, or a task conflicts with it, stop and ask** rather than guessing. Record decisions you make in `docs/DECISIONS.md` (date, decision, reason).
- **Every task includes:** the database migration (both providers), the Application use case with its permission check, the API endpoint, the UI, the tests, and updated `docs/RUNNING.md` if setup changed.
- **Tests are required** for every business rule you add. Money, permissions, printing order, idempotency and ledger balance must have unit tests. API endpoints need an integration test.
- **Definition of done:** `dotnet test` passes, `npm test` passes, `npm run lint` passes, the app starts with `docs/RUNNING.md` steps, and the screen matches its wireframe.
- **Commits:** small and in conventional style (`feat(order): print new items only`). One task per branch, named `screen/<number>-<name>` or `foundation/<part>`.
- **Never merge into `main` or create release tags.** Finish on your task branch, push it, and stop. The owner reviews and merges.
- **Never commit build output.** `target/`, `bin/`, `obj/`, `node_modules/` and `dist/` must be in `.gitignore`; check `git status` before every commit.
- **Never commit secrets.** Dev settings go in `appsettings.Development.json` with fake values; real secrets come from environment variables.
- **At the end of a task, reply with:** what you built, how to try it (exact commands and the seed PINs to use), the tests added, and anything left as a follow-up.
