# Task 0 — Foundation

Read `AGENTS.md` first and follow it throughout. This task builds the **foundation only**: no real POS screens yet. Each later task builds one screen end to end on top of this.

Work through the parts below **in order**. After each part, run the tests and commit. Stop when all parts are done, and report as `AGENTS.md` describes.

---

## Part A — Repo and tooling

1. Create the repo layout exactly as in `AGENTS.md` (`server/`, `web/`, `terminal/`, `docs/`, `.github/workflows/`).
2. **Server.** Create `Nexagen.sln` with the four projects and two test projects. Reference direction: Api → Application → Domain, Infrastructure → Application/Domain, Api → Infrastructure. Domain references nothing.
3. **Web.** Create a Vite + React + TypeScript app in `web/` with React Router, TanStack Query, ESLint, Prettier and Vitest.
4. **Terminal.** Create a Tauri v2 app in `terminal/` that loads the web app (dev: the Vite dev server; prod: the built files served by Nexagen Server — see Part G).
5. **Dev environment.** Add `docker-compose.dev.yml` with PostgreSQL 16 and, as an optional profile, SQL Server 2022 Express.
6. **Docs.**
    - `docs/RUNNING.md`: exact commands to run everything on Ubuntu from a clean clone.
    - `docs/DECISIONS.md`: start it with the stack choices.
    - `docs/design.md` and `docs/wireframes/`: create the folders. I will add the files.
7. **CI** in `.github/workflows/ci.yml`:
    - On every push: build the server and run its tests (with a PostgreSQL service), then build, lint and test the web app.
    - A second workflow, `terminal-release.yml`, builds the Tauri Windows installer on `windows-latest` when a tag `terminal-v*` is pushed.

## Part B — Database and core model

Create the EF Core `NexagenDbContext`, with migrations for **both** PostgreSQL and SQL Server (separate migration assemblies or folders; choose one approach and record it in DECISIONS.md). Add these entities now; later tasks will add fields as needed:

- **BusinessSettings** (single row): name, logo file id, address, phone, VAT rate (default 16), currency (KES), rounding rule, receipt footer, print mode (`OneButton` | `SendAndBill`, default `OneButton`), and a JSON column for other settings.
- **Licence:** key, business name, terminal limit, expiry, last checked.
- **Role** (Waiter, Cashier, Supervisor, Admin seeded; businesses can add more).
- **RolePermission:** role, permission key, value (`Allowed` | `NeedsApproval` | `NotAllowed`).
- **User:** name, role, PIN hash, active, must-change-PIN flag, failed-attempts counter, locked-until.
- **Terminal:** name, device token hash, dispense printer, receipt printer, last seen, app version.
- **Printer:** name, connection mode (`Network9100` | `WindowsShare` | `ViaTerminal`), address (IP:port or share name), owning terminal (for ViaTerminal), paper width (58/80), status, last error.
- **AuditEntry:** time, user, terminal, action, entity type and id, old value JSON, new value JSON, approval-by user.
- **IdempotencyRecord:** key, user, endpoint, response status, response body, created at.
- **PrintJob:** printer, kind (`Bill` | `Dispense` | `Void` | `Receipt` | `CashUp` | `Test`), content (ESC/POS bytes or a template + data), status (`Queued` | `Sent` | `Failed`), attempts, last error, created at, related ticket id.
- **LedgerAccount** (seed: Cash, M-Pesa, Bank, Card clearing, Sales, VAT payable, Stock, Cost of sales, Stock loss, Expenses, Supplier payables, Rounding) and **JournalEntry** + **JournalLine** (account, debit, credit, memo, source type and id).

Add a row-version / concurrency token to every editable entity.

## Part C — Auth, terminals and permissions

1. **Terminal registration.**
    - An admin registers a terminal once: the terminal app shows a pairing code, and an admin enters it in a simple dev endpoint for now (the admin screen comes in a later task).
    - The terminal stores a long-lived device token in `tauri-plugin-store`, or in `localStorage` when running in a plain browser during dev.
    - Every request carries the device token, so the server always knows which terminal it is.
2. **PIN login API.**
    - `POST /api/auth/pin` returns a short-lived session (JWT or cookie; choose one and record it).
    - The session is valid only together with the terminal's device token.
    - After 5 wrong PINs in 2 minutes, that terminal is locked for 60 seconds.
    - PINs are hashed (PBKDF2 or Argon2), and two active users can't share the same PIN.
3. **Admin site login:** a user name plus password, for Admin and Supervisor roles only, separate from the PIN. Keep it simple: cookie auth.
4. **Permission service:**
    - `IPermissionService.Check(user, key)` → `Allowed` | `NeedsApproval` | `NotAllowed`.
    - `Require(user, key, approvalToken?)` throws a typed `PermissionDeniedException`, or `ApprovalRequiredException` with the key.
5. **Approvals:**
    - `POST /api/auth/approve` takes a supervisor PIN and a permission key, and returns a single-use approval token valid for 60 seconds.
    - The token is checked by `Require`, and both users are written to the audit entry.
    - The approval-request queue (supervisor's own counter) comes in a later task; build only the token mechanism now.
6. **Error format:** the API returns a consistent problem-details JSON for permission errors. `NeedsApproval` returns HTTP 403 with `"approvalRequired": true` and the permission key, so the UI can open the approval pad.

## Part D — Cross-cutting services

1. **Audit:** `IAuditLog.Write(...)`, used by login, permission changes and settings changes now.
2. **Idempotency:** middleware for all `POST`/`PUT`/`DELETE` that honours the `Idempotency-Key` header (see `AGENTS.md`). Add tests for a repeated key and for the same key with a different body (that returns 422).
3. **Money:** a `Money` value object and a `VatCalculator` in Domain implementing the rules in `AGENTS.md`. Test it heavily, covering zero VAT, exempt items, rounding edges, quantity × price and line sums.
4. **Ledger:** `ILedgerPostingService.Post(JournalEntryDraft)`, which rejects unbalanced entries. Test it.
5. **Live updates:** set up the SignalR hub `/hubs/pos`, authenticated with the device token plus session, with an `IPosEvents` publisher in Application. The web app connects and reconnects automatically.
6. **Settings:** `GET`/`PUT /api/settings/business` (`settings.manage`), plus logo upload to local file storage served at `/files/{id}`.

## Part E — Printing engine

1. **Transports:** `IPrinterTransport` with three implementations:
    - `Network9100`: raw TCP to IP:9100.
    - `WindowsShare`: Windows only; skip it gracefully on Linux with a clear error.
    - `ViaTerminal`: sends the job to the owning terminal over SignalR. The terminal app prints it locally using a Tauri command; stub the Rust side with a TODO if needed.
2. **`PrintDispatcher`:** a background service that takes queued jobs, sends them, retries 3 times with backoff, marks them `Failed`, and publishes `print.failed`.
3. **`ReceiptBuilder`:** builds ESC/POS output from simple text templates, at 42 columns for 80 mm and 32 for 58 mm. It supports bold, double height, centred text, a separator line, a cut, and the logo printed as a raster image if present.
4. **Test print:** `POST /api/printers/{id}/test` (`devices.manage`) prints the business name, printer name, date and time, and "Test OK".
5. **Fake transport for dev:** a `FilePrinterTransport`, selected by config, that writes each job as readable text to `./print-output/` so print layouts can be checked without a printer.

## Part F — Web app shell and design system

1. **Design tokens** from `AGENTS.md` in `tokens.css`, plus self-hosted IBM Plex Sans and Mono.
2. **Shared UI components** (only what's needed now): `Button` (primary, secondary, danger, sizes ≥44 px), `TopBar`, `Keypad` (the PIN pad from wireframe 1), `Modal`, `Toast`, `Banner` (offline, print failed), `Table` (for admin lists), `Field` (label + input), `Toggle`.
3. **Routing:**
    - `/pos/*`: terminal screens. For now, a placeholder "Logged in as <name> on <terminal>" page after a working PIN login, using the real API.
    - `/admin/*`: admin layout with the sidebar from the wireframes, nav items present, pages showing "Coming in a later task".
4. **API client:**
    - Typed fetch wrapper that adds the device token, session and `Idempotency-Key` automatically.
    - When the server returns approval required, it opens a shared **ApprovalPad** modal (supervisor PIN), retries with the token, and shows a toast on denial.
5. **Connection status:** an offline banner shown when the API or SignalR is unreachable, with automatic reconnect.

## Part G — Packaging and running

1. **Static files:** Nexagen Server serves the built web app from `wwwroot`, so production needs only the server plus terminals.
2. **Terminal app:** full-screen, with no window decorations, autostart enabled, and the server address plus device token held in the store. A first-run screen asks for the server address and shows the pairing code.
3. **Server as a service:** runs as a Windows Service (`UseWindowsService`) or a systemd service (`UseSystemd`). Document both in `docs/RUNNING.md`.
4. **Health:** `GET /api/health` reports server version, database status and printer statuses.

## Part H — Seed data (for development and for Mau's Village)

Make it a command, `dotnet run --project src/Nexagen.Api -- seed --demo`, that is idempotent:

- **Business:** name "Mau's Village", VAT 16%, KES, one-button print mode.
- **Roles and default permissions** (use exactly these):

| Permission | Waiter | Cashier | Supervisor | Admin |
| --- | --- | --- | --- | --- |
| order.add, order.cancel_unprinted, ticket.print, ticket.move, ticket.merge, reports.own | Allowed | Allowed | Allowed | Allowed |
| order.void | NeedsApproval | NeedsApproval | Allowed | Allowed |
| order.gift, order.discount | NotAllowed | NeedsApproval | Allowed | Allowed |
| ticket.reprint | NeedsApproval | Allowed | Allowed | Allowed |
| order.change_price, stock.count, stock.adjust | NotAllowed | NotAllowed | NeedsApproval | Allowed |
| ticket.settle | NotAllowed | Allowed | Allowed | Allowed |
| cash.expense | NotAllowed | Allowed | Allowed | Allowed |
| stock.receive, stock.waste, cash.book, reports.all, day.open_close, staff.manage | NotAllowed | NotAllowed | Allowed | Allowed |
| menu.edit, menu.edit_prices, permissions.manage, devices.manage, settings.manage, erp.manage | NotAllowed | NotAllowed | NotAllowed | Allowed |

- **Users:**
    - Administrator (Admin), with an admin-site password from an environment variable
    - MAU (Supervisor)
    - CAROL (Cashier)
    - edna, CYNTHIA, Ruth and WAMBOO (Waiters)

  Give each a random 4-digit PIN, printed once to the console.
- **Terminals:** Counter A, Counter B, Pillar 1, Pillar 2.
- **Printers**, using the file transport in dev:
    - Ticket Printer 1 → Counter A, used as Dispense A and as the receipt printer for Counter A and Pillar 1
    - Pillar 1 → Dispense A
    - Ticket Printer 2 → Counter B, used as Dispense B and as the receipt printer for Counter B and Pillar 2
    - Pillar 2 → Dispense B
- **Ledger accounts** as listed in Part B.
- **Do not seed menu, tables or stock yet.** Those come with their screens.

---

## Acceptance checklist

- [ ] A clean clone builds and runs on Ubuntu by following `docs/RUNNING.md`.
- [ ] CI is green, and the Windows terminal build workflow exists.
- [ ] A seeded user can log in with their PIN in the browser and in `npm run tauri dev`, and sees "Logged in as … on Counter A".
- [ ] 5 wrong PINs lock that terminal for 60 seconds, and the lock is audited.
- [ ] Calling an endpoint protected by `order.void` as a Waiter returns approval required. With a supervisor approval token it succeeds once, fails if the token is reused, and the audit entry shows both users. Use a temporary test-only endpoint.
- [ ] A repeated `Idempotency-Key` returns the original response.
- [ ] `VatCalculator` and `LedgerPostingService` tests pass, including unbalanced-entry rejection.
- [ ] A test print through the file transport writes a readable slip to `./print-output/`. Through `Network9100` against a fake TCP listener in tests, the bytes arrive.
- [ ] The admin layout shows the full sidebar from the wireframes, and the business name and logo come from settings.
- [ ] The offline banner appears when the server is stopped and clears when it restarts.

When done, report as described in `AGENTS.md`, then wait for the **Screen 1 — PIN login** task.
