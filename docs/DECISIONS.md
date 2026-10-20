# Decisions

Each entry: date, decision, reason.

## Stack

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | Server on .NET 10 (LTS), ASP.NET Core minimal APIs, SignalR, EF Core 10. | Set in AGENTS.md. |
| 2026-10-19 | PostgreSQL 16 for Linux and development, SQL Server 2022 Express for Windows customers, one EF Core model. | Set in AGENTS.md. |
| 2026-10-19 | Web UI: React 19 + TypeScript + Vite, React Router 8, TanStack Query 5, our own components. | Set in AGENTS.md. |
| 2026-10-19 | Terminal: Tauri v2 with the store, autostart and updater plugins. | Set in AGENTS.md. |
| 2026-10-19 | Tests: xUnit + FluentAssertions **7.x** (server), Testcontainers PostgreSQL (integration), Vitest + Testing Library (web). | FluentAssertions 8 and later need a paid licence for commercial use; 7.x is Apache-2.0. |
| 2026-10-19 | TypeScript pinned to 6.0, ESLint (flat config) + typescript-eslint, Prettier. | The Vite template now uses TypeScript 7 and oxlint; the task asks for ESLint, and typescript-eslint supports TypeScript below 6.1. |
| 2026-10-19 | SkiaSharp (MIT) turns the logo into a printer raster. | ImageSharp's licence is not free for every commercial user; SkiaSharp is MIT and ships Linux and Windows native libraries. |
| 2026-10-19 | `System.Security.Cryptography.Pkcs` referenced directly at 10.0.x. | SqlClient pulls in 9.0.11, which has known vulnerabilities. |

## Database

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | One model in `NexagenDbContext`; small subclasses `PostgresNexagenDbContext` and `SqlServerNexagenDbContext` own their migrations in `Infrastructure/Migrations/Postgres` and `Infrastructure/Migrations/SqlServer`. `Database:Provider` picks one. | Keeps the four-project layout from AGENTS.md (no extra migration assemblies) while each provider gets its own migration history and model snapshot. Provider differences (jsonb, index filters) live in the subclasses. |
| 2026-10-19 | Row versions are an app-managed `long RowVersion` concurrency token on every editable entity (`VersionedEntity`), bumped in `SaveChanges`. | Behaves the same on both providers (SQL Server `rowversion` and PostgreSQL `xmin` differ) and in SQLite unit tests. |
| 2026-10-19 | Enums are stored as their names; money as `decimal(18,2)`; VAT rate `decimal(5,2)`. | Readable data, safe enum reordering. |
| 2026-10-19 | The Application layer references EF Core (not a provider) and uses `INexagenDb`, which exposes the DbSets. | One class per use case without a repository layer per entity. Domain stays free of EF. |
| 2026-10-19 | The server applies migrations and the base seed (roles, default permissions, ledger accounts) on start, retrying every 10 s while the database is unreachable. | A fresh install works without a manual step, and the server still starts and reports the problem on `/api/health`. |

## Auth and permissions

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | PIN session = signed JWT (HMAC-SHA256) carrying the user, terminal and session ids. It is accepted only when the request's device token (`X-Device-Token`) belongs to that same terminal. The user and role are read fresh from the database on each request. | Works for the terminal app and SignalR without cookies across origins; a stolen session is useless without the device token; deactivating a user or changing a role applies at once. |
| 2026-10-20 | **Replaces the fixed 12-hour PIN session.** Each PIN login creates a server-side `UserSession`. It ends after `pos.idleLogoutMinutes` (default 10) without an authenticated request, after 12 hours at most, on Log out, or when someone else logs in on the same terminal. Every authenticated request refreshes it, except hub connections (they are not "use"). An ended session answers 401 with `"code": "session_expired"`. The token's own expiry is the 12-hour maximum, checked with the server's clock. | A terminal left logged in must not keep working for whoever walks up. One user per terminal at a time matches how counters work. |
| 2026-10-20 | `pos.idleLogoutMinutes` and `pos.logoutAfterSettle` (default off; used by the settle task) live in `BusinessSettings.OtherJson` as `{"pos":{"idleLogoutMinutes":10,"logoutAfterSettle":false}}`, read through `PosSettings` with defaults and validated when saved. | Small settings that do not need their own columns; the task names them with a `pos.` prefix. |
| 2026-10-20 | The web app counts idle time from the last request the server saw, not from the last tap, and logs out at that moment. A tap or key press sends a keep-alive (`GET /api/auth/session`) when the last request is more than 30 s old. Any session end clears all cached data and returns to the PIN pad. | Counting from the same event as the server means the screen never shows a user the server has already logged out, while a user who is busy on screen stays logged in. |
| 2026-10-20 | A user with `MustChangePin` can only change their PIN, log out and read basics until they have (server-side gate, 403 `"code": "pin_change_required"`). Rejected PINs: all one digit, runs up or down (e.g. 1234, 9876), the current PIN, and any other active user's PIN ("Choose a different PIN", without saying whose). The seed never hands out such PINs. | The server decides, not the UI. |
| 2026-10-20 | `User.PinLength` is stored so `GET /api/auth/pin/max-length` (paired terminals only) can return the longest PIN length in use as a bare number. Users from before this change count as 6 digits. | The pad can submit by itself without revealing anything about which PINs exist; old rows' lengths cannot be read from their hashes. |
| 2026-10-19 | Admin site = cookie auth (`nexagen_admin`, HttpOnly, SameSite=Strict, 12 h sliding). Which roles may sign in is data: `Role.CanUseAdminSite` (seeded true for Supervisor and Admin). | Task asks for simple cookie auth; AGENTS.md forbids checks on role names. |
| 2026-10-19 | PINs: PBKDF2-SHA512 (210,000 iterations) plus an HMAC-SHA256 lookup key with a server pepper. A unique index on the lookup key (filtered to active users) stops two active users sharing a PIN. | Login finds the user in one query; uniqueness is enforced by the database; a stolen database alone cannot be brute-forced without the pepper. |
| 2026-10-19 | Server secrets (PIN pepper, session signing key) come from `Nexagen:PinPepper` / `Nexagen:JwtSigningKey` when set, else are generated once into `<data>/secrets`. | No secret in the repo; the data directory must be backed up with the database. |
| 2026-10-19 | Wrong PINs are counted per terminal in `PinFailures`; 5 within 2 minutes lock the terminal 60 s (HTTP 429 with `Retry-After`). A correct PIN clears the count. Wrong supervisor PINs on the approval pad count too. Failures and the lock are audited. | As specified; approval PINs must not be a way around the lockout. |
| 2026-10-19 | Admin-site passwords: 5 wrong in a row lock that user for 5 minutes (uses `FailedAttempts` / `LockedUntil` on User). | The task lists these fields; the PIN lockout is per terminal. |
| 2026-10-19 | Approval tokens: random, stored hashed in `ApprovalGrants`, single use (a conditional update marks them used), 60 s, tied to one permission key. Sent as `X-Approval-Token`. `Require` re-checks that the approver is still active and Allowed. `PermissionGrant.ApprovedByUserId` goes into the audit entry. | Single use holds even with two simultaneous requests. |
| 2026-10-19 | Permission errors: 403 problem details with `permission` and `approvalRequired` (true only for NeedsApproval). | The UI opens the approval pad only when it can help. |
| 2026-10-19 | Terminal pairing: the app gets a 6-digit code (10 minutes) plus a secret only it knows; an admin enters the code at `POST /api/terminals/pair` (`devices.manage`); the app collects its device token once with the secret. Pairing again replaces the token. | Simple for staff; the token never appears on screen. |

## Staff

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-20 | "Who may manage whom" uses tiers from data: built-in roles carry a stable `Role.SystemKey` (waiter, cashier, supervisor, admin); Admin = admin tier, Supervisor = manager tier, Waiter/Cashier = staff tier. **Added roles count as staff tier (Waiter-level) unless they have `staff.manage`** (then manager tier). Managers manage staff tier only; admins manage everyone except their own role and their own deactivation; the last active admin can't be deactivated. | Task 2, rule 5. The key, not the display name, decides, so renaming a role changes nothing and no code tests role names. |
| 2026-10-20 | People a supervisor may not manage are left out of their list and answer 404 if addressed directly; a supervisor can't edit themselves either (not even their own password). | "Hidden in the UI, and the server rejects them" without confirming who exists. |
| 2026-10-20 | When the only active admin tries to deactivate themselves, the answer is "There must be at least one active Admin."; with another admin present it is "You can't deactivate yourself". | The more useful reason first. |
| 2026-10-20 | Names are unique ignoring case and surrounding spaces across **all** people, active or not (`User.NormalizedName`, unique index). | The task says names are unique among active users and that deactivating doesn't free a name; together, a name is never reused. |
| 2026-10-20 | Temporary PINs appear only in the create, reset-PIN and reactivate answers. The idempotency store keeps a copy of those answers with `temporaryPin` blanked, so a replayed request gets `null` (the manager resets again if the first answer was lost). Audit entries never hold PINs, passwords or their hashes. | PINs are only ever stored hashed. |
| 2026-10-20 | Reset PIN and Deactivate end the person's sessions on every terminal and publish `user.pin_reset` / `user.deactivated` (`{ userId }`); a terminal showing that person returns to the PIN pad at once. | Acceptance: back to the PIN screen within 2 seconds after a reset too. |
| 2026-10-20 | Admin-site sign-in name: the person's name in lower case (spaces become dots, a number added if taken), set when a password is first given, e.g. MAU signs in as `mau`. A password is required when adding someone whose role uses the admin site; when editing it is optional ("set / reset"). | Simple for staff; existing names (e.g. `admin`) are kept. |
| 2026-10-20 | The one-time PIN message is "Give this PIN to Grace. They must change it at first login." | The task's example said "She"; a name doesn't tell us anyone's pronouns, so the screen doesn't guess. |
| 2026-10-20 | The staff screen's right-side panel opens when needed (Add staff button, Edit, Reset PIN, Reactivate) instead of always showing the add form. Roles are listed Waiter, Cashier, Supervisor, Admin, then added roles; the first is the default. | The task adds columns the wireframe doesn't have (last login, terminals, three actions); with the panel always open the actions were cut off at 1280 wide. The wireframe says "Waitress"; the role is called Waiter. |

## Menu and prices

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-20 | **Order lines copy their price.** When an item is ordered, its price (and option add-ons, VAT rate and the gross/net/VAT split) is copied onto the order line. Changing a menu price afterwards never changes a line already on a ticket. The order line itself comes with the order screen. | Tickets and receipts must not change under people's feet; the price log explains the menu, the lines explain the sale. |
| 2026-10-20 | Price fields are accepted only by the price endpoints (`PUT /api/admin/menu/items/{id}/prices`, `PUT /api/admin/menu/groups/{id}/prices`), which need `menu.edit_prices`. The structure requests refuse unknown JSON members, so a price sent to them is a 400 even if unchanged. The UI's one Save button makes two calls. | Task 3: clean permissions; a user with `menu.edit` alone can never change a price. |
| 2026-10-20 | The menu admin opens for anyone whose role has `menu.edit` or `menu.edit_prices` set to anything but Not allowed; each change then applies its own permission (with the approval token when it is Needs approval). | Viewing is not an action worth an approval. |
| 2026-10-20 | A new item can be created without prices (by someone with `menu.edit` only); it is not sold on terminals until it has a price on the default list, and the admin list shows "No price". The default-list price can't be removed, and a list can't become the default while active items lack a price on it. | Keeps "details" and "prices" permissions separate without blocking menu building. |
| 2026-10-20 | Price-log rows cover item prices and option add-ons (old, new, who, approver, terminal or admin site, time, source `admin` or `import`). A price of 0 needs `allowZero` (the screen asks first); the import refuses 0 and asks for the admin screen instead. | A free item is usually a mistake. |
| 2026-10-20 | `menuVersion` is a hash of the terminal's menu (its price list and everything sold), so it changes exactly when what the terminal shows changes. Terminals refetch on `menu.changed`, after reconnecting, and every minute as a fallback. | No counter to keep in step; per-terminal by nature. |
| 2026-10-20 | Option groups given to a category apply to all its items; items can add their own. The export's `option_groups` column holds only an item's own groups, so export → import changes nothing. Option add-ons are prices (see above); options removed from a group are deactivated, never deleted. | Round trip stable; history intact. |
| 2026-10-20 | CSV import: auto-maps headers (`price` = default list, `price2` = "Price 2", any price-list name; `category`, `name`, `short_name`, `vat_rate`, `active`, `option_groups`, `erpnext_item_code` and common SambaPOS variants), else asks for a mapping. Comma or semicolon separated; UTF-8 if valid, else Windows-1252. Empty price cells leave prices alone. Rows with errors stop the apply unless "Skip rows with errors" is on; the apply is one transaction. Changing any price by import needs `menu.edit_prices`. | Task 3, Part F. |
| 2026-10-20 | API-only for now: `PUT /api/admin/terminals/{id}/price-list` (`devices.manage`) and `PUT /api/admin/roles/{id}/permissions` (`permissions.manage`, audited). | The acceptance steps need them before the Terminals and Permissions screens exist. |
| 2026-10-20 | The POS tables placeholder shows a read-only "Menu (for testing)" list (`web/src/pos/TestMenu.tsx`). **Remove it in the order-screen task.** | Task 3, Part E: check terminal prices before the order screen. |

## Idempotency

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | Middleware on `/api` POST/PUT/PATCH/DELETE. A record holds key, caller (user, else terminal, else anonymous), endpoint and a SHA-256 of the body. Repeat → stored answer with `Idempotency-Replayed: true`; same key with another caller, endpoint or body → 422; repeat while the first is running → 409. Requests without the header pass through. | As specified; the header is sent by our UI on every write but other clients (curl, health checks) still work. |
| 2026-10-19 | Only successful (2xx) answers are stored; a failed request's key is released. | A 403 "approval required" must not be replayed when the UI retries with the approval token under the same key; failed actions changed nothing, so retrying is safe. |

## Printing

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | `ReceiptBuilder` and `ReceiptTemplate` live in Application (pure code); transports and the dispatcher in Infrastructure. A job stores the ESC/POS bytes and a text preview. | Use cases build slips without I/O; the file transport and the screen can show the preview. |
| 2026-10-19 | Templates: one instruction per line (`@logo`, `@cut`, `@feed n`, `---`, `===`, `[center][bold][big]text`, `left || right`, `{{placeholder}}`). Data is inserted as text, never parsed as instructions. Long text wraps. | Simple enough to edit; names are never cut off. |
| 2026-10-19 | "Retries 3 times": 4 attempts in all, waiting 2 s, 4 s, 8 s; then `Failed` and `print.failed`. Jobs for one printer go strictly in queue order, so a bill waiting to retry holds back the dispense slip behind it. | Keeps the bill-then-dispense order even when the first try fails. |
| 2026-10-19 | `Nexagen:Printing:Transport = File` sends every job to `FilePrinterTransport`; `Real` (the default) picks by the printer's connection mode. | As specified. The integration tests still use the global switch. |
| 2026-10-20 | Printers have a fourth connection mode, `File`, served by `FilePrinterTransport`. Development no longer sets the global switch; the demo printers are File printers instead. | A printer's own setting says where its slips go, as in production. |
| 2026-10-19 | Via terminal: the server calls the terminal's SignalR connection with a client result (`print.job`) and waits up to 15 s for `{ ok, error }`; the app calls the Tauri command `print_raw`. | The server learns whether it printed, so retries and failures work the same as other transports. |
| 2026-10-19 | Demo printers are seeded as Windows-share printers (`\\COUNTER-A\TicketPrinter1`, `\\COUNTER-B\TicketPrinter2`) with their owning terminal set to show where they are plugged in. "Dispense A" is Ticket Printer 1, "Dispense B" is Ticket Printer 2. | Mau's server is Windows with USB printers on counter PCs; the real share names are set on site in the Terminals screen. |
| 2026-10-20 | **Updated:** in the Development environment `seed --demo` creates the demo printers with the `File` connection, so test prints land in `print-output/`; running it again in Development also moves untouched Windows-share placeholders to `File`. Elsewhere they stay Windows-share placeholders. The foundation task's list describes two physical printers (Ticket Printer 1 = Dispense A, Ticket Printer 2 = Dispense B) used by four terminals, so two printers are seeded, not four. | Test prints work in development without editing printers. The Screen 1 task mentions "four demo printers"; the foundation list reads as two printers and four terminal routes, so that was kept (see follow-ups). |
| 2026-10-20 | **Replaces the two-printer seed.** Four printers, one per terminal: Ticket Printer 1 (Counter A), Pillar 1, Ticket Printer 2 (Counter B), Pillar 2. Each terminal dispenses on its own printer. "Dispense A" / "Dispense B" are templates, stored on the printer as `DispenseTemplate` with the printed header in `DispenseHeader` ("Counter A" / "Counter B"); Pillar 1 uses Dispense A and Pillar 2 Dispense B. Re-running the seed adds the pillar printers and moves only routes still pointing at the counter's printer (anything set by hand stays); old print jobs keep their printer. | The owner's correction (task 2). |
| 2026-10-20 | **Pillar receipts print at the counter**, as at Mau's today and in wireframe 17: Counter A and Pillar 1 print receipts on Ticket Printer 1, Counter B and Pillar 2 on Ticket Printer 2. Re-running the seed moves a pillar's receipts back to its counter only if they still point at the pillar's own printer (the task 2 seed); hand-set routes stay. It is a per-terminal setting for the Terminals screen. | The owner's choice after review (task 2 had put pillar receipts on the pillar printers). |

## Web and terminal

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | The web app always calls same-origin URLs (the Vite dev server proxies `/api`, `/hubs`, `/files`). Only the terminal app's bundled setup screen calls the server across origins; CORS allows just the Tauri origins on the anonymous pairing, brand and health endpoints. | Production is one origin; no general CORS opening. |
| 2026-10-19 | The terminal app bundles the web build for its first-run screen, then navigates to `<server>/pos`. Pages from the server call the app's commands through a remote capability for `http(s)://*:*/*`. In development the window always loads Vite and is a normal 1280×800 window (`NEXAGEN_FULLSCREEN=1` for the counter look). | Production UI updates come with the server; the app only needs a new release for Rust changes. The server address is chosen at install time, so the capability cannot name it in advance (see follow-ups). |
| 2026-10-19 | The PIN session is kept in `sessionStorage`; the device token in `localStorage` (browser) or the Tauri store. | A reload does not log the user out, closing the tab does. |
| 2026-10-19 | The offline banner shows when a request or the 5 s health check fails, or while SignalR reconnects; health is checked every 3 s while offline. SignalR reconnects for ever (0.5 s doubling to 10 s, then a full restart every 5 s). | Clears within seconds when the server is back. |
| 2026-10-19 | `/api/brand` (name, logo, currency) and `/api/health` are anonymous. | The login screen shows them before anyone signs in. |
| 2026-10-20 | The hub accepts a paired terminal with only its device token (policy `DeviceOrSession`), so the PIN screen gets `day.opened` and other events before anyone logs in. Unpaired clients are refused. | Every terminal's login screen must update when the day opens. Payloads are small and carry no secrets. |
| 2026-10-20 | `WorkPeriod` (opened/closed at, by, terminal) with an `IsOpen` column and a filtered unique index (one open day). Opening needs `day.open_close`, is audited and publishes `day.opened`. The current day is part of `GET /api/device`. Closing comes with cash-up. | Enforced by the database even when two terminals open the day at once. A column rather than "ClosedAt is null" because PostgreSQL before 15 treats NULLs as distinct in unique indexes. |
| 2026-10-20 | After login the terminal goes to `/pos/day`, which moves straight on to `/pos/tables` when the day is open, or offers Open the day (users with `day.open_close`) or "Ask a supervisor" with Back (others; Back logs out). | One place decides what comes after login. |
| 2026-10-19 | `/api/dev/void-probe` (guarded by `order.void`) exists only in Development and Testing. | The task asks for a temporary test-only endpoint to prove the approval flow. |

## Follow-ups

- **Terminal printing (Rust):** `print_raw` in `terminal/src-tauri/src/lib.rs` is a stub. Implement RAW printing to the local Windows queue (OpenPrinter / StartDocPrinter "RAW" / WritePrinter).
- **Updater:** the updater plugin is installed but has no public key or endpoint yet (`plugins.updater` in `tauri.conf.json`). Generate signing keys, publish `latest.json`, turn on `createUpdaterArtifacts`.
- **Tighten the Tauri remote capability** from `http(s)://*:*/*` to the paired server's origin (e.g. add the capability at runtime after pairing).
- **Remove `/api/dev/void-probe`** and `DevVoidProbe` when the real void task lands.
- **Admin pairing screen:** the Terminals and printers task should replace the curl step with a "Pair terminal" dialog using `POST /api/terminals/pair`.
- **"Not printed · Retry":** the print-failed banner only offers Close; add a retry endpoint for failed jobs.
- **Approvals on the admin site:** `POST /api/auth/approve` needs a terminal. Supervisors on the admin site rarely need it, but decide whether to support it.
- **Licence checks:** the `Licence` table exists; checking and the grace period come with the setup wizard.
- **Backups:** nightly database backup plus the data directory (it holds the PIN pepper).
- **Server installer:** a Windows installer for Nexagen Server (service + SQL Server Express) and a Linux package.
- **Edit roles and permissions** (Permissions screen): until then, added roles can only be created in the database.
- **Approvals on the admin site:** an action that needs approval can't be approved there yet (the approval pad needs a terminal).
- **Remove the test menu list** (`web/src/pos/TestMenu.tsx`) with the order-screen task.
- **CI runner image:** GitHub notes that `ubuntu-latest` moves to Ubuntu 26 from 19 Oct 2026; pin `ubuntu-24.04` if that causes trouble.
- **Close the day:** `WorkPeriod` has the closing fields; closing comes with cash-up.
- **Logout after settle:** `pos.logoutAfterSettle` exists but is not acted on until settle is built.
