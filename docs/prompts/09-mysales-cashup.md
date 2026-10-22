# Task 9 — My sales, cashier shifts, cash-up and closing the day

Read `AGENTS.md` first. Use the wireframes **"8 · My sales (waiter)"** and **"9 · Cashier cash-up"** in `docs/wireframes/`. Work on a new branch `screen/09-mysales-cashup` from `main` (tag `screen-08-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This task closes the money loop for the night:
- waiters see their own figures and print a slip
- each cashier runs a **shift** on a drawer, from opening float to a counted, signed-off cash-up
- a manager **closes the day**, which is blocked while any ticket or shift is still open (the owner's decision, in `design.md` under "Closing the day")

**Not in this task:**
- The full cash book, expense categories admin and the daily summary report come in Task 12. This task only adds the two drawer actions a cash-up needs: **pay out** and **cash drop** (Part B).
- Admin reports for all waiters come in Task 13.

---

## Part A — Cashier shifts (domain and API)

**`CashierShift`:**
- cashier
- terminal (the drawer)
- work period
- opening float
- opened at
- closed at
- status: `Open`, `Closed`
- denomination counts (JSON)
- counted cash
- expected cash
- difference
- note
- signed off by
- row version

**Rules:**
- **One open shift per cashier, and one per terminal.** A second cashier can't open a shift on a drawer that's in use. The error says "Counter A's drawer is in CAROL's shift. CAROL must close it first."
- **Opening a shift:**
  - It needs an **open work period** and `ticket.settle`.
  - The float is entered by denomination, or as a total.
  - It defaults to `cash.defaultFloat`, a new setting with a default of 0.
- **Settling needs a shift:** from now on, **settling** (Task 8) requires the cashier to have an open shift **on the terminal being used**. Otherwise the settle screen shows **"Start your shift"**, with the float entry inline, and then carries on. Every payment records its `ShiftId`.
- **Expected cash** = opening float + cash applied in settles (not the amount tendered) − pay-outs − cash drops. M-Pesa and card totals are shown for checking, and aren't counted in the drawer.
- **Closing a shift:**
  1. The cashier enters the **count** by denomination: 1,000, 500, 200, 100 and 50 notes, plus coins entered as one amount. The note list comes from a setting `cash.denominations`, which defaults to the Kenyan notes.
  2. A user with `day.open_close` **signs off** with their PIN, using the Task 7 approval component. They can't be the cashier.
  3. The difference is stored and posted (Part C).
  4. The **cash-up slip** prints (Part E).
- **Blind count:** a setting `cashup.blind`, **on by default**. While it's on, the cashier doesn't see the expected cash or the difference until the count is submitted. The supervisor sees both on sign-off. This is the usual way to stop cashiers counting towards the expected figure. Record it in DECISIONS.md.
- **After closing:** a closed shift can't be changed. A recount after sign-off is a follow-up (Part H).

**Endpoints:**
- `POST /api/pos/shifts` (open)
- `GET /api/pos/shifts/current`
- `POST /api/pos/shifts/{id}/count`
- `POST /api/pos/shifts/{id}/close`, with the approval token
- `GET /api/pos/shifts/{id}/summary`

Every write takes `Idempotency-Key`, and every action is audited.

## Part B — Pay out and cash drop (drawer actions)

These live on the shift screen, for the cashier who owns the open shift.

- **Pay out:** cash taken out of the drawer for an expense.
  - The fields are the amount, a **category** and a note (required).
  - Seed the categories: Supplies, Transport, Casual labour, Repairs, Other. Task 12 adds an admin screen for them.
  - It needs `cash.expense`. With `NeedsApproval`, it uses the approval component.
- **Cash drop:** cash moved from the drawer to the safe. The fields are the amount and a note (optional). It needs `ticket.settle`.
- **Both:**
  - They can't take the expected drawer cash below 0.
  - They're audited.
  - Each prints a small slip with the amount, who, when and the reason, so it can be kept with the cash.

## Part C — Ledger

**Accounts:** add **Safe** and **Cash over/short**, if they're missing.

| Event | Debit | Credit |
| --- | --- | --- |
| Opening float | Cash | Safe |
| Pay out | Expenses (with the category on the line) | Cash |
| Cash drop | Safe | Cash |
| Close, short | Cash over/short | Cash |
| Close, over | Cash | Cash over/short |

**Cash left in the drawer:** after the count, what's left in the drawer is moved to **Safe** (debit Safe, credit Cash), so the drawer is back at 0 for the next shift.

**Every entry** carries the shift, the user and the work period, and must balance.

## Part D — The cash-up screen (terminal app)

Match wireframe 9. The route is `/pos/shift`.

**Getting there:** cashiers get a **My shift** button in the top bar of the table map, next to Approvals.

**With no open shift:** the screen shows **Start shift**, with the float entry.

**With an open shift, there are three columns:**
- **Expected in the drawer:**
  - opening float
  - + cash settled
  - − pay-outs
  - − cash dropped to the safe
  - **Expected cash**
  - the M-Pesa count and total ("Check against the till statement")
  - the card total

  With a blind count, this column shows "Shown after you count".
- **Count the cash:** one row per denomination, each with a number box and a subtotal. Coins are one amount. Then **Counted**, and **Difference** (± in orange when it's not 0). The difference only shows after the count is submitted if the count is blind.
- **Cash brought by waiters:** for this shift, each waiter's name, the cash they brought, and their **open bills** (in orange when more than 0). Under it, the note: "Open bills on a waiter's name stay open after your shift. The day can't close until they're settled."

**Buttons:**
- **Pay out**
- **Cash drop**
- **Submit count**
- **Close shift · supervisor PIN to sign off**, the primary button

**Open tickets don't block a cashier's shift.** The next cashier can settle them, and only closing the **day** is blocked. Record this in DECISIONS.md: the wireframe's note said open bills "must be settled or handed over", and handing them over just means leaving them open.

## Part E — Printed slips

All slips use the shared template code, times in `business.timeZone`, at 42 and 32 columns.

- **Cash-up slip:**
  - "CASH-UP · Counter A · CAROL", with the shift's open and close times
  - float, cash settled, pay-outs (each with its category), drops, **expected**
  - the count by denomination, **counted**, and the **difference**
  - M-Pesa (count and total) and card
  - cash brought by each waiter
  - the number of receipts and the first to last receipt number
  - "Signed off by MAU", and a signature line
- **Waiter slip** (from My sales): the waiter's name and the work period, total sales, the number of tickets, cash / M-Pesa / card brought, and their open bills listed by ticket number.
- **Pay-out slip and drop slip:** small slips, as described in Part B.
- **Day report** (Part F).

## Part F — Closing the day

**Screen `/pos/day`:** for users with `day.open_close`, reached from a **Close day** button on the table map's top bar, shown only to them.

**It shows what blocks the close, each item with a link:**
- **Open tickets:** number, table, waiter, total, and "Part paid" where it applies. Use the Task 8 check.
- **Open cashier shifts:** cashier and terminal.
- **Pending approval requests.** These are expired automatically when the day closes. They don't block it.

**When nothing blocks, it shows a summary of the day:**
- the number of tickets
- gross sales
- discounts, gifts (at their original value) and voids
- net sales and VAT by rate
- payments by type
- rounding
- the cash-up differences per shift
- total pay-outs

**Close day** (needs `day.open_close`):
1. Closes the work period.
2. Prints the **day report** to this terminal's receipt printer, with the summary above under the heading "DAY REPORT · Z", the period's open and close times, and who closed it.
3. Logs out every POS session, after a 60-second warning toast on every terminal: "The day is closing. Finish what you're doing."
4. Publishes `day.closed`.

**Development command:** keep `close-day` for development only, using the same use case and the same checks.

## Part G — My sales (waiter)

Match wireframe 8. The route is `/pos/my-sales`, behind the **My sales** button from Task 5. It needs `reports.own`.

**Period:** the current work period. The header says "My sales · edna · today", meaning this work period. Record the wording change from "this shift" in DECISIONS.md, since waiters don't have shifts.

**Left column:**
- **Total sales:** the value of lines **added by me** that aren't voided or cancelled, after their discount share. Gifts count as 0. Open and settled tickets both count.
- **Tickets:** the number of tickets with at least one line added by me.
- **Brought to the cashier:** the payments where **brought by** is me, split into Cash, M-Pesa and Card.
- **Bills still open on your name:** the number of open tickets I opened, in orange when it's more than 0. Tapping it lists them with their totals.
- **Print my cash-up slip.**

**Right column:** my lines in this period, showing the ticket, time, item (with modifiers), quantity and amount.
- Long names wrap.
- Voided lines are shown struck through with "Void", and gifts with "Gift".
- The list is scrollable, newest first.

**Privacy:** the header says "Only your own figures are shown", and the API never returns other users' data from this endpoint. Supervisors see everyone in the admin reports (Task 13).

**Endpoint:** `GET /api/pos/my-sales`, plus `POST /api/pos/my-sales/print`.

## Part H — Follow-ups to record (don't build)

- Reopening or recounting a closed shift, with the manager's approval.
- Several cashiers sharing one drawer.
- A bank deposit from the safe (Task 12's cash book).
- A daily email of the day report.

## Part I — Default permissions and settings

**Permissions** (set only where they aren't set yet):

| Permission | Waiter | Cashier | Supervisor | Manager |
| --- | --- | --- | --- | --- |
| `reports.own` | Allowed | Allowed | Allowed | Allowed |
| `cash.expense` | NotAllowed | NeedsApproval | Allowed | Allowed |
| `day.open_close` | NotAllowed | NotAllowed | Allowed | Allowed |

**Settings:**
- `cash.defaultFloat` = 0
- `cash.denominations` = [1000, 500, 200, 100, 50], plus coins
- `cashup.blind` = true

## Part J — Tests

**Server:**
- **Shifts:**
  - One per cashier and one per terminal, including the error text.
  - A shift needs an open work period.
  - Settling with cash needs the user's open shift on that terminal, and payments get the `ShiftId`.
- **Expected cash:** float + cash applied (not tendered) − pay-outs − drops, across mixed payments and part payments.
- **Pay out and drop:**
  - They're refused if they'd take the drawer below 0.
  - `cash.expense` works with Allowed, NeedsApproval and NotAllowed.
  - Their slips print.
- **Close:**
  - Sign-off needs `day.open_close`, and the cashier can't sign their own shift.
  - A blind count hides the expected cash and the difference from the cashier's API responses until the count is submitted.
  - A closed shift can't be changed.
  - Two sign-offs of the same shift at once: exactly one wins.
- **Ledger:** every entry balances: float, pay-out, drop, short, over, and the drawer moving to the safe at close.
- **Closing the day:**
  - It's blocked by open tickets, part-paid tickets and open shifts, and the lists are returned.
  - Pending requests are expired when it closes.
  - The summary adds up exactly to the period's receipts and payments.
  - The `close-day` dev command uses the same checks.
  - Sessions are logged out after the close.
- **My sales:**
  - Totals use lines added by me, after their discount share, with voids out and gifts at 0.
  - Brought-by totals are per payment type.
  - Open bills count tickets I opened.
  - It never returns another user's lines, checked as a security test with two users.
- **Golden slips:** cash-up (normal and short), waiter slip, pay-out, drop and day report, at 42 and 32 columns.

**Web:**
- **Shift screen:**
  - starting a shift with a float
  - the count subtotals and the counted total
  - blind count on and off
  - sign-off through the approval component
- **Settle without a shift:** shows "Start your shift" inline, and then carries on.
- **Close day screen:** blockers with their links, the summary, and the 60-second warning toast.
- **My sales:** the figures, the open-bills list, wrapping names, and void and gift markers.

## Acceptance checklist

- [ ] As CAROL on Counter A, press Settle on a ticket. "Start your shift" asks for a float of 2,000, and settling then carries on.
- [ ] Settle three tickets, one with cash 500 for 370, one with M-Pesa, and one with card. Pay out 200 for Transport, and drop 1,000 to the safe.
- [ ] With a blind count, CAROL counts 1,170 and submits. Only then does she see Expected 1,170 (2,000 + 370 − 200 − 1,000) and a difference of 0. MAU signs off with his PIN, and CAROL's own PIN is refused. The cash-up slip prints.
- [ ] Count 100 short on a second test shift. The slip and the ledger show the 100 short under Cash over/short.
- [ ] As edna, My sales shows only her lines, her brought-in cash, and 1 open bill. Her slip prints.
- [ ] As MAU, Close day lists edna's open ticket and any open shift, and refuses to close. After they're settled and closed, Close day prints the DAY REPORT, every terminal gets the warning, and everyone is logged out.
- [ ] Every journal entry from these flows balances.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/09-mysales-cashup` and stop.

When done, report as `AGENTS.md` describes, with what I should test on the real PCs, printer and cash drawer, and anything you decided differently from this prompt.
