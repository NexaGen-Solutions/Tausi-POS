# Task 5 — Table map

Read `AGENTS.md` first. Use the wireframe **"2 · Table map"** in `docs/wireframes/`. Work on a new branch `screen/05-table-map` from `main` (tag `screen-04-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This is the first screen of the terminal app after login. It needs tickets to exist, so this task also builds the **ticket model and the ticket operations that live on the table map** (open, new, move, merge, and locking). Adding order lines and printing come in Task 6. Before you start, check what the foundation already built (Ticket, OrderLine, WorkPeriod, approval tokens, SignalR events) and **extend it rather than duplicating it**. Note in `DECISIONS.md` anything you changed.

---

## Part A — Areas and tables (admin)

Not in the wireframes. Add a small **Tables** page to the admin site (`/admin/tables`, in the sidebar between "Terminals and printers" and "Reports"). Access needs `settings.manage`. Use the same layout as the other admin lists.

- **Areas:** name, sort order, active (e.g. Main, Pool, Bar).
- **Tables:** name (text, so "7", "Pool 1" and "Bar 2" all work), area, sort order, active.
- **Quick add:** "Add tables from [1] to [16] in [Main]" creates them in one go and skips names that already exist.
- A table with open tickets can't be deactivated or deleted. Say which tickets are open.
- Publish `tables.changed` so terminals redraw without a restart.
- **Seed (`--demo`):** Mau's layout. Tables 1–16 in Main, Pool 1–2 in Pool, Bar 1–2 in Bar.

## Part B — Tickets (domain and API)

**Ticket:**
- number
- table (null for takeaway)
- customer name (optional, used for takeaway)
- status: `Open`, `Settled`, `Merged`, `Cancelled`
- opened by
- opened at
- terminal
- work period
- last activity at
- bill printed at (filled in by Task 6)
- note
- row version

**Rules:**
- **Numbers:** ticket numbers are one global sequence. They are never reused, and they're assigned when the ticket is first saved.
- **No empty tickets:** an empty ticket is never saved. Opening a free table or tapping "New ticket" opens a **draft** on the terminal. The draft is saved when its first order line is saved, which comes in Task 6. Leaving a draft with no lines just discards it.
- **Work period:** a ticket can only be created inside an **open work period**. If none is open:
  - the map shows a banner, "The day isn't open yet".
  - users with `day.open_close` also get an **Open day** button.
  - new tickets are blocked.
- **Total:** the ticket total is the sum of its non-voided lines' gross amounts, computed on the server. Don't store a total that can drift from its lines.
- **Visibility:** any user with `order.add` can open any open ticket. Lines keep recording who added them. Record this in DECISIONS.md, since SambaPOS behaves the same way.

**Takeaway:** a takeaway ticket has no table. The Takeaway tab lists open takeaway tickets (number, customer name or "Takeaway", waiter, total, age) and has a **New takeaway** button.

**Endpoints (POS, session required):**
- `GET /api/pos/tables` returns:
  - areas and tables
  - for each table, its open tickets: number, opened-by name, total, opened at, last activity, and whether it's open on a terminal and by whom
  - a `version`

  Use one query, not one per table.
- `GET /api/pos/tickets/takeaway`
- `GET /api/pos/tickets/{id}`
- `POST /api/pos/tickets/{id}/move` with `{ toTableId }`, needing `ticket.move`
- `POST /api/pos/tickets/merge` with `{ ticketIds[], intoTicketId }`, needing `ticket.merge`

Writes take `Idempotency-Key`, as `AGENTS.md` describes.

## Part C — Ticket locking (one terminal edits at a time)

- **Lock on open:** opening a ticket on a terminal takes an **edit lock**, held by that terminal and user.
  - The terminal renews it every 30 seconds while the ticket is on screen.
  - It's released when the user leaves the ticket, logs out or is logged out for being idle.
  - If it isn't renewed for 90 seconds, it expires.
- **Locked elsewhere:** a second terminal opening the same ticket gets it **read-only**, with a banner: "Open on Pillar 1 by Ruth. You can view it but not change it." When the lock is released, the banner updates live and editing becomes possible.
- **Move and merge** need every ticket involved to be unlocked, or locked by the same terminal. Otherwise they're refused with the same plain message.
- Server operations also lock the ticket rows in the database transaction (rule 8), and use the row version.
- Publish `ticket.changed` (id, table, status) and `ticket.lock` (id, terminal, user or null).

## Part D — The table map screen (terminal app)

Match wireframe 2.

**Top bar:**
- the business name from settings
- "Counter A · edna (Waitress)" (terminal, user and role)
- **My sales**, a placeholder route until Task 9
- **Log out**

**Tabs:**
- **All tables**
- **My tables**: tables with at least one open ticket opened by me, or with a line added by me
- **Takeaway**

**Grid:**
- Tables are grouped by area in area order, then by table sort order.
- Each area gets a small heading when there's more than one area.
- The grid fills the screen. With more tables than fit, the area scrolls. **Tiles never shrink below 120 × 88 px**, so they stay easy to tap.

**Tile:**
- the table name
- the running total of its open tickets
- the waiter's name, or "name · 2 tickets" when there are several
- a small lock icon while one of its tickets is open on another terminal

**Colours** (use the `AGENTS.md` tokens, and the legend in the wireframe):
- **Free:** white
- **Occupied:** blue `#1F5FA8`
- **Unpaid, idle:** primary `#B34A0C`. A table goes orange when it has an open ticket with no activity for `pos.tableIdleMinutes` (a new setting, default 30, under Settings → POS). Activity means a line added, voided or printed, a move or a merge. Show the legend as "Unpaid, idle 30 min" using the real setting value.
- Colour must never be the only signal. Add the word "Idle" with its minutes (e.g. "Idle 42 min") on orange tiles.

**Tapping:**
- **Single tap** selects a table and fills the right panel.
- **Double tap**:
  - on a free table, opens a new draft.
  - on a table with one ticket, opens it.
  - on a table with several tickets, does the same as a single tap.

**Right panel** (match the wireframe):
- **Free table:** "Table 4 · Free" and a big **New ticket** button.
- **Occupied table:**
  - "n open ticket(s)", listing each one's number, waiter, total and age. Tapping a ticket picks it, and the first is picked by default.
  - **Open ticket**
  - **New ticket on this table**
  - **Merge tickets**, shown only when the table has two or more tickets
  - **Move to another table**
- **Merge:** pick two or more tickets on this table with checkboxes. The **oldest one is kept**. Confirm with "Merge #1047 into #1042?"
- **Move:** the map switches to a "Choose a table for #1042" mode with a Cancel button. Tapping any active table moves the ticket there. If that table already has tickets, the moved one becomes another ticket on it (no automatic merge).
- **Permissions:** when a user's permission for move or merge is `NeedsApproval`, show the supervisor PIN pop-up and send the approval token with the request. Build it as a **reusable component**, since Task 7 extends it. When the permission is `NotAllowed`, hide the button.

**Order screen route:**
- Opening a ticket or draft goes to `/pos/ticket/{id|new}?table=…`. For now that's a placeholder showing the header (table, ticket number or "New ticket", user), the existing lines read-only, and **Back to tables**. Task 6 builds the real screen there.
- The placeholder must already take and release the edit lock, so locking can be tested.

**Live and offline:**
- The map updates live from `ticket.changed`, `ticket.lock` and `tables.changed`, with no full reload: patch the affected tables, and refetch only if the `version` jumps.
- If the SignalR connection drops, show a banner ("Not connected to the server. Reconnecting…") and disable every action until it's back. Then refetch.

**Idle logout:** the existing idle logout also applies on this screen.

## Part E — Merge and move rules (server)

**Merge:**
- Lines move to the kept ticket **unchanged**: their added-by, terminal, time, printed/new status and price stay as they were.
- The other tickets get status `Merged`, with `MergedIntoTicketId` set. Their numbers stay in history and reports.
- The note fields are joined.
- Audit it with: who, terminal, the ticket numbers and the totals before and after.
- Only open tickets on the **same table** can be merged. Merging takeaway tickets is not in v1.

**Move:**
- The table changes, and nothing else on the ticket changes.
- Audit it with the from-table and to-table.
- Nothing is printed. Record a follow-up in DECISIONS.md: "optional move slip to the dispense printer".

**Both:**
- They update "last activity" and publish `ticket.changed` for every table involved.
- Settled, merged and cancelled tickets don't appear on the map.

## Part F — Tests

**Server:**
- **Numbers:** the sequence never repeats under concurrent first-saves (an integration test with parallel requests on both providers).
- **Totals:** computed from lines, with voided lines excluded.
- **Work period:** no ticket can be created when no work period is open.
- **Locks:**
  - take, renew and release the lock
  - expiry after 90 seconds, using a fake clock
  - the second terminal is read-only
  - the lock is released on logout and on idle logout
- **Merge:**
  - lines keep added-by and terminal
  - the oldest ticket is kept
  - merged tickets get the `Merged` status and link
  - merge is refused across tables, for non-open tickets, and when a ticket is locked elsewhere
  - it's audited
- **Move:**
  - it works onto a free table
  - it works onto an occupied table, which ends with two tickets
  - it's refused onto an inactive table and when the ticket is locked elsewhere
- **Permissions:** move and merge with Allowed, NeedsApproval (valid, expired and reused token) and NotAllowed.
- **Idle status:** goes on after `pos.tableIdleMinutes` with no activity, and off after activity (fake clock).
- **Tables endpoint:**
  - one query, checked with a query-count assertion
  - the expected shape
- **Admin tables:**
  - quick add skips names that already exist
  - a table with open tickets can't be deactivated

**Web:**
- **Grid:** groups by area, and the tile states render (free, occupied, idle with the word "Idle", lock icon).
- **My tables:** the filter is correct.
- **Tap behaviour:** single tap and double tap behave as described.
- **Merge and move flows:** they work, including the approval pop-up.
- **Live updates:** an incoming `ticket.changed` updates just that tile.
- **Offline:** actions are disabled and the banner shows.
- **Locked elsewhere:** the banner shows in the placeholder.

## Acceptance checklist

- [ ] With `seed --demo`, log in as a waiter on a browser terminal. The map shows tables 1–16, Pool 1–2 and Bar 1–2, grouped by area. The seed also includes a few open tickets with lines, so occupied tiles, totals and an idle tile are visible.
- [ ] With no open work period, the map shows the banner and blocks new tickets. A manager can open the day from the banner.
- [ ] Open the same ticket on two terminals. The second gets the read-only banner and becomes editable within 2 seconds of the first one leaving.
- [ ] Merge two tickets on Table 7. One ticket remains, with all lines still showing their original waiter. Both terminals update live. The audit log has the entry.
- [ ] Move a ticket from Table 7 to Table 4. Both tiles update live on every terminal.
- [ ] Set a waiter's `ticket.merge` to NeedsApproval. Merging asks for a supervisor PIN and works with it.
- [ ] Set `pos.tableIdleMinutes` to 1. A table with no activity turns orange, shows "Idle 1 min", and the legend reads "Unpaid, idle 1 min".
- [ ] Stop the server. The banner shows and actions are disabled. Start it again, and the map recovers on its own.
- [ ] All tests, lint and CI pass. Push `screen/05-table-map` and stop.

When done, report as `AGENTS.md` describes, with the seed PINs to use and anything you decided differently from this prompt.
