# Task 1 — Screen 1: PIN login

Read `AGENTS.md` first. Use the wireframe **"1 · PIN login"** in `docs/wireframes/`. Work on a new branch `screen/01-pin-login` from `main`. The foundation is merged and tagged `foundation-v1`.

The foundation already has a basic PIN login. This task makes it production-ready, and fixes two foundation decisions. Do only what is listed here; anything else goes in `docs/DECISIONS.md` under Follow-ups.

---

## Part A — Fix two foundation decisions

1. **Session lifetime.** Replace the fixed 12-hour PIN session with:
    - **An idle timeout** enforced by the server: setting `pos.idleLogoutMinutes`, default **10**. Each authenticated request refreshes it, and a request after the timeout returns 401 `session_expired`.
    - **An absolute maximum** of 12 hours, as now.
    - **Logout after settle:** setting `pos.logoutAfterSettle`, default **off**. Add the setting now; settle itself comes in a later task.

    The web app returns to the PIN screen on `session_expired` and keeps nothing from the previous user on screen. It also counts idle time locally and logs out at the same moment, so the screen never shows a user who is already logged out.

    Update the entry in `DECISIONS.md`.
2. **Dev printers.** In the `Development` environment, `seed --demo` creates the four demo printers with the **File** transport, so test prints land in `./print-output/`. Outside Development, keep the Windows-share placeholders. Update the entry in `DECISIONS.md`.

## Part B — The login screen (match wireframe 1)

Layout: 1280×800, two parts.

**Left panel** (dark, 520 px):
- "TERMINAL" label and the terminal's name, e.g. Counter A.
- The **business logo** (if set) and **business name** from BusinessSettings. Never hard-code "Mau's Village".
- The day's state:
    - "Work period open since 08:14" when a day is open, or
    - "Day not open" when not.
- At the bottom, a connection line: a green dot and "Connected to <server name or address>", or an orange dot and "Reconnecting…" (reuse the foundation's offline detection).

**Right panel:**
- The heading "Enter your PIN".
- **PIN dots:** show as many dots as digits typed (4 to 6 digits allowed). Never show the digits.
- **Keypad:** 3×4 (1–9, Clear, 0, Enter) with 80 px keys and IBM Plex Mono digits. Enter is the primary colour.
- **Physical keyboard works too:** digits, Backspace, Enter and Escape (clear). Use a listener scoped to this screen, not a global one.
- **Auto-submit** when the PIN reaches the longest length used by any active user (the server provides this number, without revealing anything about which PINs exist). Enter also submits.
- The helper line: "Each person has their own PIN, so every order and void is recorded under their name."

**States:**
- **Wrong PIN:** the dots shake, they clear, and "PIN not recognised" shows for 3 seconds. Don't say whether the user exists.
- **Locked:** after 5 wrong PINs in 2 minutes (foundation rule), show "Too many attempts. Try again in 0:47" with a live countdown. The keypad is disabled until it ends. The lock applies to the terminal and is audited, as already built.
- **Server unreachable:** keypad disabled with "Can't reach the server. Retrying…". Recovers automatically.
- **Inactive user:** treated the same as a wrong PIN.

## Part C — First login: change PIN

When a user has `MustChangePin = true` (new staff, or after an admin resets their PIN), they don't go in straight after logging in:

1. A screen asks them to **choose a new PIN** (4–6 digits), then **enter it again**. It uses the same keypad component.
2. The server rejects:
    - a PIN used by another active user, with the message "Choose a different PIN". Don't say who has it.
    - sequences like 1234 or 0000. The blocked list lives in code: all-same digits, ascending and descending runs.
    - the same PIN as the current one.
3. On success, `MustChangePin` becomes false, the change is audited, and they continue in.

Endpoint: `POST /api/auth/pin/change`. It needs the current session, and the new PIN is hashed as in the foundation.

## Part D — Opening the day

After a successful login:

- **If a work period is open:** go to `/pos/tables`. For this task, show a placeholder page: "Table map: coming in Screen 2" with the top bar (business name, `<terminal> · <user> (<role>)`, **Log out** button).
- **If no work period is open:**
    - Users **with** `day.open_close` see "The day isn't open yet" and a primary **Open the day** button. It creates a WorkPeriod (opened by, opened at, terminal), audits it, publishes `day.opened` over SignalR, and continues to `/pos/tables`.
    - Users **without** it see "The day isn't open yet. Ask a supervisor to open it." and a **Back** button to the PIN screen.
    - When `day.opened` arrives, every terminal's login screen updates its left panel straight away.

Add the minimal **WorkPeriod** entity if the foundation didn't: id, opened at/by/terminal, closed at/by/terminal (nullable). Only one open at a time, enforced in the database. **Closing** the day is out of scope (it comes with cash-up), but add the field.

## Part E — Log out

- **Log out button:** the placeholder tables page's top bar has a **Log out** button. It ends the session on the server, audits the logout, and returns to the PIN screen.
- **Idle logout:** this also returns to the PIN screen, with a small note "Logged out after 10 minutes without use" shown for 5 seconds.

## Part F — Tests

**Server (unit and integration):**
- Login with a correct PIN, a wrong PIN, an inactive user, and from an unpaired terminal (rejected).
- Lockout after 5 wrong PINs, its expiry, and the audit entry.
- Idle timeout: a request after the timeout returns `session_expired`; a request just before refreshes it. Use a fake clock.
- Absolute 12-hour maximum.
- PIN change:
    - success
    - mismatch (handled in the UI only)
    - a PIN taken by another user
    - a blocked sequence
    - the same as the old PIN
    - check that `MustChangePin` is cleared
- Open the day:
    - allowed with `day.open_close`
    - forbidden without it
    - a second open day is rejected
    - the `day.opened` event is published
- The max PIN length endpoint returns only a number.

**Web (Vitest + Testing Library):**
- Typing on the keypad and on the keyboard fills the dots, and Backspace and Escape work.
- Auto-submit at the max length, and the wrong-PIN state clears the dots.
- The lockout countdown disables the keypad and re-enables it at zero.
- The must-change-PIN flow, including a mismatch.
- The "day not open" paths for both kinds of user.
- `session_expired` returns to the PIN screen with no previous user data visible.

## Acceptance checklist

- [ ] The screen matches wireframe 1 at 1280×800. The business name and logo come from Settings; change them via the API and the screen updates after a refresh.
- [ ] Log in with keypad touches only, and with keyboard only.
- [ ] A wrong PIN shakes and clears. 5 wrong PINs show a countdown and lock only that terminal.
- [ ] Reset a user to `MustChangePin` with a dev command or seed flag. They must choose a new PIN, and 1234 and another user's PIN are refused.
- [ ] With no day open: a waiter is told to ask a supervisor. MAU can open the day, and other terminals' login screens update without a refresh.
- [ ] After 10 minutes idle (use a 1-minute setting to test), the terminal returns to the PIN screen, and the old session is refused by the server.
- [ ] The Log out button works and is audited.
- [ ] In Development, `seed --demo` puts printers on the File transport, and a test print appears in `./print-output/`.
- [ ] All tests, lint and CI pass. `docs/RUNNING.md` is updated if any command changed.

When done, report as `AGENTS.md` describes, including the seed PINs to try and how to trigger must-change-PIN.
