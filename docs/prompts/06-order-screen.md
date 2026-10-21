# Task 6 — Order screen, Cold/Warm pop-up and Print

Read `AGENTS.md` first. Use the wireframes **"3 · Order screen (waiter)"** and **"4 · Cold / Warm pop-up"** in `docs/wireframes/`. Work on a new branch `screen/06-order-screen` from `main` (tag `screen-05-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This is the screen waiters use all night. It must feel instant, survive a terminal crash without losing items, and print **the customer bill first, then the dispense slip with only the new items**. It replaces the ticket placeholder from Task 5, at the same route `/pos/ticket/{id|new}`.

**Not in this task:**
- Void, gift, discount and on-ticket price changes. They come in Task 7, so show those buttons disabled with the tooltip "Comes with voids and approvals".
- Settling, which comes in Task 8.
- Stock deduction, which comes in Task 11.

---

## Part 0 — Carry-overs from Task 5 (do these first, as separate commits)

1. **Old open tickets disappear from the map.** If it isn't already fixed: open tickets must show on the map whatever work period they belong to. Add a test where a ticket from a closed work period still shows after a new day is opened. `seed --demo` must add its demo tickets to an existing database if they're missing, without duplicating them.
2. **EF Core warning.** Fix the `First`/`FirstOrDefault` without `OrderBy` warning in the seed.
3. **Release the lock when a ticket closes abruptly.** When the browser tab closes or reloads, call the release endpoint with `navigator.sendBeacon` on `pagehide`. In the Tauri app, do it on the window close event. The 90-second expiry stays as the safety net.
4. **Remove the temporary menu check** from the ticket placeholder.

## Part A — Order lines (domain and API)

Extend the Task 5 `OrderLine`:
- menu item
- name and short name, copied at add time
- the chosen modifier options, copied as text plus option ids
- quantity
- unit price, copied from the terminal's price list
- VAT rate, copied
- line gross (`quantity × unit price`)
- status: `New`, `Printed`, `Cancelled` (`Voided` comes in Task 7)
- print round (null until printed)
- added by
- terminal
- added at
- printed at
- note

**Rules:**
- **Saving:** every change is saved to the server **as it happens**, not held on the terminal, so a crash or a power cut loses nothing.
  - The first line of a draft creates the ticket, using the `POST /api/pos/tickets` from Task 5.
  - Later lines use `POST /api/pos/tickets/{id}/lines`.
- **Prices and VAT:**
  - The price comes from the **terminal's price list**, and never from the request body.
  - Items with no price on that list aren't shown.
  - Once added, a line keeps its copied price and name, even if the menu changes.
- **Added by:** added-by and terminal always come from the session.
- **Same item again:** adding an item that matches an existing `New` line on this ticket (same item, same options, same price, same user) increases that line's quantity instead of adding a new line. Printed lines are never changed.
- **New lines can change:**
  - Their quantity can go up or down.
  - Taking a `New` line to 0, or tapping **Remove**, cancels it. This needs `order.cancel_unprinted`.
  - Cancelled lines are kept for the audit trail, and don't show on the ticket or the bill.
- **Printed lines can't change here.** Their quantity is locked. Changes go through Void in Task 7.
- **Edit lock:** every write needs the Task 5 edit lock, held by this terminal. Otherwise it's refused with the "Open on … by …" message.
- **Modifiers:** a required modifier group (e.g. Temperature: Cold / Warm) must have exactly its required count. The server validates this, and it can't be skipped.
- Every write takes `Idempotency-Key`. Publish `ticket.changed`, so the map total updates live.

**Endpoints:**
- `POST /api/pos/tickets/{id}/lines` with `{ menuItemId, optionIds[], quantity, note? }`
- `PATCH /api/pos/tickets/{id}/lines/{lineId}` with `{ quantity }`, for New lines only
- `DELETE /api/pos/tickets/{id}/lines/{lineId}`, which cancels a New line
- `PATCH /api/pos/tickets/{id}` with `{ note?, customerName? }`
- `POST /api/pos/tickets/{id}/print`
- `POST /api/pos/tickets/{id}/reprint-bill`

## Part B — The order screen (terminal app)

Match wireframe 3.

**Header:**
- Tables
- "Table 7 · Ticket #1042", or "Table 7 · New ticket" for a draft
- "Takeaway · [customer name]" with an editable name, for a takeaway ticket
- the terminal and user on the right

**Left: the ticket.** Each line shows:
- quantity
- name
- modifiers (e.g. "Cold")
- who added it and the time ("edna · 19:42")
- amount
- a **New** or **Printed** badge

Tapping a line selects it, with a blue bar on the left.

Under the lines:
- **"Selected: …"** with actions:
  - for a **New** line: **−**, **+** and **Remove**
  - for a **Printed** line: **Void** and **Gift**, both disabled until Task 7
- The **total** in large monospace.
- **Print (n new)**, the primary button:
  - it shows the number of new lines
  - with nothing new, it reads **Print** and acts as a reprint (Part D)
- **Reprint bill**
- **Settle**:
  - disabled and labelled **"Settle: cashier only"** for users without `ticket.settle`
  - for cashiers, a placeholder route until Task 8
- The helper line from the wireframe: "Print: the customer's bill with every item first, then a slip with only the n new items to Dispense A". Fill it in from the routing.

**Middle: categories.** Use the menu's category order, with the selected category in black. **Ticket note** and **Move table** sit at the bottom. Move reuses the Task 5 flow.

**Right: the menu.**
- **Search** across all categories by name or short name. Pin it to the top, and make it work with the Windows touch keyboard.
- An **item grid** of the selected category, showing each item's name and price. Tiles are at least 120 × 88 px.

**Tapping an item:**
- **With no required modifiers:** adds 1 straight away.
  - The line appears **immediately** (optimistic) and turns solid when the server confirms.
  - If the server refuses, the line is removed with the reason in a toast.
  - Taps are queued in order, so fast tapping never loses or reorders adds.
- **With a required modifier group:** opens the **Cold / Warm pop-up** (wireframe 4):
  - the item name, and "Temperature · choose 1 (required)"
  - big option buttons, with the selected one highlighted
  - a quantity control with − and +
  - **Cancel**
  - a primary button reading **"Add 2 × Tusker (Cold) · KSh 500"**

  The primary button is disabled until the required choice is made. If the item has a default option, pre-select it. Optional groups appear under the required one, with "optional" written next to them.
- **Menu updates:** on `menu.changed` the menu reloads quietly. Lines already added keep their prices.

**Leaving the ticket with New lines:** **Tables**, a logout or a move asks: "2 items haven't been printed. Print now / Leave them unprinted / Stay".
- **Leave them unprinted:** the lines stay saved as New.
- **On the map:** the tile shows a small "2 not printed" note, so they aren't forgotten.
- **Idle logout:** keeps them without asking.

**Read-only mode:** if the ticket is locked by another terminal, everything is disabled and the Task 5 banner shows.

## Part C — Print (one button)

`POST /api/pos/tickets/{id}/print` needs `ticket.print` and runs in **one transaction**, with the ticket row locked:

1. Gives the ticket its next **bill number** ("Bill 1", "Bill 2" and so on, per ticket).
2. Marks every `New` line as `Printed`, with this print round and the time.
3. Creates the print jobs **in this order**:
    1. **Customer bill**, with **all** non-cancelled lines, to this terminal's **receipt printer**.
    2. **Dispense slip**, with **only this round's lines**, to this terminal's **dispense printer**, under its dispense header.

    Use the Task 4 queue and templates, rendered by the same code as the test print.
4. Writes the audit entry and publishes `ticket.changed`.

The bill always goes into the queue first, so when one printer does both (as at Mau's), the bill comes out first, then a cut, then the dispense slip.

**Customer bill (80 mm, 42 columns; 58 mm, 32 columns):**
- Logo (if `print.logoOnBill`), business name, address and phone.
- "BILL 2 · Ticket #1042". From bill 2 on, add a second line: "Replaces earlier bills".
- Table (or "Takeaway · name"), the waiter who opened the ticket, the terminal, and the date and time.
- **Lines:** quantity, name and modifiers, then the amount right-aligned.
  - Identical lines (same item, options and price) are **added together** on the bill.
  - Long names wrap. They're never cut off.
- The **TOTAL** in large text.
- One line per VAT rate: "Total includes VAT 16%: KSh 189.66".
  - VAT = gross × rate ÷ (100 + rate), rounded half away from zero (`AGENTS.md`).
  - Exempt items are listed as "VAT exempt: KSh …".
- The receipt footer from settings, then "Powered by Tausi POS" if `print.poweredByFooter` is on.
- "This is not a receipt" in small text. The final receipt comes with settling.

**Dispense slip:**
- The **dispense header** (e.g. "COUNTER A") in large text.
- Table, ticket number, and the time.
- "By: edna", meaning who added these lines. If several people did, list each one with their lines.
- **No prices.** Each line is the quantity and **short name** in double height, with the modifiers on the next line ("COLD").
- The line note and the ticket note, if there are any.

**Failures:** if a job fails, the order screen shows a banner, **"Bill not printed on Ticket Printer 1 · Retry"**, using the Task 4 retry endpoint. The banner appears live from `print.failed`, and it also shows when the ticket is reopened while a job is still failed. The lines stay `Printed`, because they were sent and printing is retried. Never reprint them as new.

**Print mode setting** (`print.mode`):
- `OneButton` is the default, and is the behaviour above.
- `SendAndBill` shows two buttons instead:
  - **Send (n new)** prints only the dispense slip and marks the lines Printed.
  - **Print bill** prints only the bill.

  The same permissions apply. Add the setting to the existing print settings. The Settings screen comes in Task 10.

## Part D — Reprint

- **Print with nothing new**, or **Reprint bill**, prints only the customer bill:
  - with the next bill number
  - marked **"REPRINT"** under the bill number
- It needs `ticket.reprint`. With `NeedsApproval`, it uses the Task 5 supervisor PIN pop-up. With `NotAllowed`, the button is disabled with "Reprint needs a supervisor".
- Audit it with: who, the approver if there was one, the bill number and the total.
- A dispense slip is **never** reprinted from the order screen. A lost slip is redirected with Retry, or sent to another printer from the admin Failed jobs.

## Part E — Seed and the permission grid

**Seed `--demo`:** make sure Tusker, Guiness, White Cap and Balozi have the required **Temperature (Cold / Warm)** group, with Cold as the default. Water, sodas and spirits stay without it.

**Default permissions** (only where they aren't set yet):

| Permission | Waiter | Cashier | Supervisor | Manager |
| --- | --- | --- | --- | --- |
| `order.add` | Allowed | Allowed | Allowed | Allowed |
| `order.cancel_unprinted` | Allowed | Allowed | Allowed | Allowed |
| `ticket.print` | Allowed | Allowed | Allowed | Allowed |
| `ticket.reprint` | NeedsApproval | Allowed | Allowed | Allowed |

## Part F — Tests

**Server:**
- **Saving lines:**
  - The first line creates the ticket and a number.
  - The price comes from the terminal's price list, and a price sent by the client is ignored.
  - An item with no price on that list is refused.
  - Added-by and terminal come from the session.
- **Same item again:** it merges into the user's own New line only. A line that is printed, by a different user, with different options or at a different price makes a new line.
- **Modifiers:** a missing required option is refused, and too many options are refused.
- **New and printed lines:**
  - New lines can change quantity, and cancelling one needs `order.cancel_unprinted`.
  - Printed lines can't be changed or deleted.
- **Edit lock:** writes without the lock are refused.
- **Print:**
  - The bill job is queued before the dispense job.
  - The bill has all lines and the dispense slip only this round's.
  - Lines become Printed with the round number.
  - The bill number goes up.
  - It all happens in one transaction: if creating a job fails, nothing is marked Printed.
- **Two terminals pressing Print together** on the same ticket: only one round prints, and the other is refused or prints a reprint. Test it in parallel on both providers.
- **Reprint:**
  - It prints only the bill, marked REPRINT.
  - Permission: Allowed, NeedsApproval (valid, expired and reused token) and NotAllowed.
  - It's audited.
- **Slip text** at 42 and 32 columns, as golden-file tests:
  - bill 1 and bill 2 ("Replaces earlier bills")
  - identical lines added together
  - a long name wrapping
  - VAT lines for 16% and for an exempt item
  - the footer on and off
  - a dispense slip with modifiers and notes
  - a dispense slip with two waiters
- **VAT:** the amounts follow the rounding rule, with edge cases.
- **`SendAndBill` mode:** Send prints the dispense slip only, and Print bill prints the bill only.

**Web:**
- **Adding:**
  - Tapping adds immediately (optimistic), and a refused add is removed with a toast.
  - Ten fast taps give a quantity of exactly 10, in order.
- **Pop-up:** the button stays disabled until a choice is made, the default is pre-selected, and the quantity and price text update.
- **Line actions:** a New line shows −, + and Remove, and a Printed line shows Void and Gift disabled.
- **Print button:** the label counts the new lines, and with nothing new it acts as a reprint.
- **Leaving with unprinted lines:** the dialog's three choices work.
- **Not printed banner:** appears and Retry works.
- **Read-only mode:** works when the ticket is locked elsewhere.
- **Settle button:** disabled for a waiter.

## Acceptance checklist

- [ ] As edna on Counter A, tap Table 4 twice to get a new ticket. Add 2 × Tusker (Cold) from the pop-up, 1 Pineapple Punch and 1 Mau's Water. The ticket gets a number, and the map on another terminal shows Table 4 with the total.
- [ ] Press **Print (3 new)** with the File transport. The output has **Bill 1** first, with all 3 lines and the VAT line, then the dispense slip "COUNTER A" with the 3 lines in large text and no prices.
- [ ] Add 1 Guiness, then **Print (1 new)**. Bill 2, "Replaces earlier bills", has all 4 lines. The dispense slip has **only** the Guiness.
- [ ] Press Print with nothing new as edna. It asks for a supervisor PIN. With MAU's PIN, it prints a bill marked REPRINT.
- [ ] Kill the browser tab mid-ticket and reopen it. Every added line is still there, and the lock was released straight away.
- [ ] Leave a ticket with 2 unprinted items and choose "Leave them unprinted". The tile shows "2 not printed".
- [ ] Point Counter A's receipt printer at an unreachable network printer and press Print. The "Bill not printed · Retry" banner shows. Set it back, press Retry, and the bill prints before the dispense slip.
- [ ] On Pillar 1 (receipt Ticket Printer 1, dispense Pillar 1), the bill goes to Ticket Printer 1 and the slip to Pillar 1.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/06-order-screen` and stop.

When done, report as `AGENTS.md` describes, with what I should test on the real Windows PC and thermal printer, and anything you decided differently from this prompt.
