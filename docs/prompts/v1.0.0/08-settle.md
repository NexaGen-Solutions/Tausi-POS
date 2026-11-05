# Task 8 — Settle (cashier only), final receipt and ledger posting

Read `AGENTS.md` first. Use the wireframe **"7 · Settle (cashier only)"** in `docs/wireframes/`. Work on a new branch `screen/08-settle` from `main` (tag `screen-07-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

At Mau's, the waiter takes the bill to the table, collects cash or sees the M-Pesa message, and brings it to the cashier. **Only the cashier settles.** For each payment the cashier records:
- the type
- the amount
- the M-Pesa code
- **who brought it**

Settling prints the final receipt, closes the ticket and posts balanced journal entries.

**Not in this task:**
- Cash-up, cash drops, opening floats and the close-day screen all come in Task 9. This task only adds the server rule for closing the day (Part G).
- Stock deduction comes in Task 11.
- Refunds and un-settling are follow-ups (Part H).

---

## Part A — Payments (domain and API)

**`Payment`:**
- ticket
- type: `Cash`, `Mpesa`, `Card`
- amount applied to the ticket
- tendered (cash only)
- change (cash only)
- M-Pesa code
- card reference (optional)
- **taken by**: the cashier, from the session
- **brought by**: a user
- terminal
- work period
- taken at
- status: `Pending`, `Final`, `Removed`

**Rules:**
- **Saving:** payments are saved **as they're added**, as `Pending`, so a crash loses nothing. The cashier can remove a pending payment, which sets it to `Removed` and audits it. At completion they all become `Final`, and after that they can't be changed.
- **Cash:**
  - The amount applied is `min(tendered, remaining)`, and the change is `tendered − applied`.
  - Tendered less than remaining is allowed, as a part payment.
- **M-Pesa:**
  - The code is required, and must be 10 letters or digits, stored in upper case.
  - It must be **unique across all payments ever**, including removed ones from other tickets, enforced by a unique index. The error says: "Code SJK3XXXXXX was already used on #1031 (Table 3, 20:14)".
  - The amount can't be more than what's remaining.
- **Card:** the amount can't be more than what's remaining. The reference is optional.
- **Brought by** is required for every type. It defaults to the **user who opened the ticket**. The list shows staff who are logged in or have worked in this work period, the default first, with their role.
- **Amount due:** the ticket total, rounded by `money.rounding`:
  - `NearestShilling` is the default.
  - `None` is the other choice.

  Store the difference as the ticket's **rounding amount** (it can be positive or negative).
- **Permission:** every payment action needs `ticket.settle`, and the cashier must hold the Task 5 edit lock. Writes take `Idempotency-Key`.

**Endpoints:**
- `POST /api/pos/tickets/{id}/payments`
- `DELETE /api/pos/tickets/{id}/payments/{paymentId}`
- `POST /api/pos/tickets/{id}/settle` (complete)
- `POST /api/pos/tickets/{id}/receipt/reprint`

## Part B — Complete (settle)

`POST /api/pos/tickets/{id}/settle` runs in **one transaction**, with the ticket row locked.

**First, it checks:**
1. **No pending approval requests.** Call the check from Task 7, which gives "Waiting for MAU to approve a void on Guiness".
2. **No unprinted (New) lines.** Otherwise it's refused with "2 items haven't been printed". The screen offers **Print, then settle**, which runs the Task 6 print and then returns to settling.
3. **Remaining is exactly 0**, after rounding.

**Then it:**
1. **Sets the ticket:** status `Settled`, with `SettledAt`, `SettledBy`, the terminal and a **receipt number**. Receipt numbers are a global sequence, separate from ticket numbers, never reused, and assigned only at completion.
2. **Finalises the payments.**
3. **Posts the journal entry** through `ILedgerPostingService`, which must balance (see Part D).
4. **Queues the final receipt** to the settling terminal's **receipt printer**, and the **cash drawer pulse** if the printer has "Open cash drawer on receipt" turned on and any payment was cash.
5. **Releases the edit lock** and expires any leftover requests.
6. **Audits it**, and publishes `ticket.changed`. The table becomes free on every map.
7. **Logs out,** if `pos.logoutAfterSettle` is on. Otherwise it goes back to the table map.

**Idempotent:** a repeated complete with the same key returns the same receipt and never posts twice.

## Part C — The settle screen (terminal app)

Match wireframe 7. The route is `/pos/ticket/{id}/settle`.

**Getting there:**
- The **Settle** button on the order screen is now live for users with `ticket.settle`. It's still disabled for waiters, with "Settle: cashier only".
- The cashier opens any table from the map, then presses Settle.
- If the ticket is locked by another terminal, the read-only banner shows and Settle is disabled.

**Left side: the bill.**
- **"Bill · all items"**, laid out as on the printed bill: gifts, voids hidden, the discount, subtotal and TOTAL.
- The VAT line(s).
- Each payment added so far, for example "M-Pesa SJK3XXXXXX − 1,000" or "Cash (edna) − 370", with an **×** to remove it while it's pending.
- **Remaining** in large text. When it reaches 0, it shows **"Paid"** in blue.
- A rounding line, if there's one ("Rounding −0.40").

**Right side: payment.**
- **Type tabs:** Cash, M-Pesa, Card.
- **Cash:**
  - "Cash for the remaining KSh 370"
  - quick buttons: the exact remaining amount, then the next round amounts (e.g. 400, 500, 1,000)
  - **Cash brought by**
  - **Cash received**, on a number pad
  - **Change**, in large text
  - **Add cash payment**
- **M-Pesa:**
  - **M-Pesa code from the SMS**, upper-cased as it's typed
  - **Amount**, defaulting to the remaining amount
  - Brought by
  - **Add M-Pesa payment**
  - the note "A code already used on another bill is rejected"
- **Card:** the amount (defaulting to the remaining amount), an optional reference, Brought by, and **Add card payment**.
- **Complete and print receipt:** the primary button, enabled only when Remaining is 0. If cash was taken, a confirmation shows the change in large text: "Give change: KSh 130".
- **Leaving:** **Back to ticket** keeps the pending payments. On the map, the table shows "Part paid · KSh 370 left" while pending payments exist.

## Part D — Ledger posting

Seed the ledger accounts from the design, if they're missing:
- Cash
- M-Pesa
- Bank
- Card clearing
- Sales
- VAT payable
- Rounding
- Stock
- Cost of sales
- Stock loss
- Expenses
- Supplier payables

**On settle, post one journal entry:**
- **Debit:**
  - Cash, for the cash applied (not the tendered amount)
  - M-Pesa
  - Card clearing
- **Credit:**
  - **Sales**, for the net amount (excluding VAT) of each line **after its discount share**, with gifts and voids at 0. Keep one journal line per VAT rate, so VAT reports can read it.
  - **VAT payable**, for each rate.
- **Rounding:** debit or credit the Rounding account, so the entry balances to the shilling.
- **Reference:** the receipt number and ticket.
- **Who and when:** the entry records the cashier and the work period.

Cost of sales and stock come in Task 11. Leave a clear place for them.

**The posting service** rejects any entry that doesn't balance, and a test proves it.

## Part E — The final receipt

Use the same template code as the bill (Task 4 and Task 6), plus:
- **"RECEIPT No. 2041 · Ticket #1042"**, with the table (or Takeaway and the customer name), the waiter, the cashier, the terminal and the date and time in `business.timeZone`.
- All items as on the bill, including gifts and the discount.
- **TOTAL**, the rounding (if any), then **each payment**:
  - "Cash 500 · Change 130"
  - "M-Pesa SJK3XXXXXX 1,000"
  - "Card 1,370 · ref 1234"
- The VAT lines ("Total includes VAT 16%: KSh …", and "VAT exempt: KSh …" for exempt items).
- The footer from settings, then "Powered by Tausi POS" if it's turned on.
- **"Served by edna · Cash brought by edna"**. List each brought-by name if there are several.
- **Reprint receipt:**
  - It prints "COPY" under the receipt number.
  - It needs `ticket.reprint`, using the Task 7 approval component.
  - It's audited.
  - It's reached from the **Settled** tab (Part F).

## Part F — Table map additions

- **Settled tab:** a new tab, **Settled**, for users with `ticket.settle`. It lists the tickets settled **in the current work period**, newest first, each showing:
  - the receipt number
  - the ticket and table
  - the total
  - the payment types
  - the cashier
  - the time

  Each ticket has **Reprint receipt**. Settled tickets can only be viewed, not changed.
- **Part paid tile:** a table with pending payments shows "Part paid · KSh 370 left" on its tile.

## Part G — Closing the day: no open tickets (owner's decision)

The owner chose this rule, and the design now says: **the day (work period) can't close while any ticket is open, including part-paid ones.**
- Add the check to the close-work-period use case, so Task 9's close screen and the dev `close-day` command both use it. Refuse with the list of open tickets: number, table, waiter and total.
- **Empty tickets:** a ticket whose lines are all voided or cancelled, and whose total is 0, may be closed as `Cancelled` by a user with `day.open_close`. Add `POST /api/pos/tickets/{id}/close-empty`. It's audited, and it's needed so that fully voided tickets don't block the day.
- **Clean-up:** remove the old demo data that kept open tickets in a closed day. Seeded demo tickets now belong to the open demo day, or the seed opens one.
- Record the decision in `DECISIONS.md`.

## Part H — Follow-ups to record (don't build)

- Refunds of a settled ticket, with approval, a refund slip and a reversing journal entry.
- Un-settling a ticket by mistake.
- M-Pesa till confirmation through the Daraja API.
- Card terminal integration.
- Tips.

## Part I — Default permissions

| Permission | Waiter | Cashier | Supervisor | Manager |
| --- | --- | --- | --- | --- |
| `ticket.settle` | NotAllowed | Allowed | Allowed | Allowed |

Set it only where it isn't set yet. A `NeedsApproval` value for `ticket.settle` is treated as NotAllowed, because settling isn't approvable. Enforce that in the permission service, and add a test.

## Part J — Tests

**Server:**
- **Payments:**
  - Cash applied and change, including a part payment.
  - An M-Pesa code that's malformed, duplicated on another ticket, or duplicated on a removed payment is refused.
  - M-Pesa and card can't be more than what's remaining.
  - Brought by is required, and defaults to the user who opened the ticket.
  - Pending payments can be removed, and final ones can't.
- **Complete:**
  - It's refused with pending approvals, with New lines, and when anything is still remaining.
  - It succeeds with mixed payments: M-Pesa 1,000 + cash 370 (tendered 500).
  - It's idempotent: two completes with the same key give one receipt and one journal entry.
  - Two cashiers settling the same ticket at once: exactly one wins, on both providers.
- **Rounding:** `NearestShilling` with a discount that leaves cents, and `None`. The rounding is posted to the Rounding account.
- **Ledger:**
  - The entry balances for cash, M-Pesa, card, mixed payments, a discount, a gift, a void, mixed VAT rates and rounding.
  - Sales are recorded net of VAT and after each line's discount share.
  - An entry that doesn't balance is rejected.
- **Receipt numbers:** they never repeat when settled in parallel, on both providers.
- **Golden slips:**
  - the receipt with cash and change, with M-Pesa and cash, with a discount and a gift, and with rounding
  - the COPY reprint
  - each at 42 and 32 columns
- **Cash drawer pulse:** sent only on cash settles, and only when the printer has it turned on.
- **Closing the day:**
  - It's refused with open or part-paid tickets, and the list is returned.
  - It's allowed after they're settled.
  - `close-empty` works only for zero-total tickets, and needs `day.open_close`.
- **Permission:** settling with `NeedsApproval` is treated as NotAllowed.
- **After settle:** `pos.logoutAfterSettle` on and off.

**Web:**
- **Settle screen:**
  - Remaining updates as payments are added and removed.
  - The quick cash amounts are right.
  - Change is shown in large text.
  - The M-Pesa code is upper-cased, and a duplicate shows the server's message.
  - Complete stays disabled until Remaining is 0.
- **Settling with unprinted lines:** "Print, then settle" works.
- **Map:** the Settled tab shows its tickets and Reprint receipt (with approval), and a part-paid tile shows its note.
- **Waiters:** the Settle button is disabled for them.

## Acceptance checklist

- [ ] As edna, order on Table 7 and Print. Log in as CAROL (Cashier) on Counter A, open Table 7, and press Settle.
- [ ] Add M-Pesa SJK3ABC123 for 1,000, then cash 500 for the rest, brought by edna. Change shows 130. Complete prints the receipt with both payments and "Cash brought by edna", and the drawer pulse is in the output.
- [ ] Table 7 is free on every terminal. The journal entry balances: Cash 370 + M-Pesa 1,000 = Sales (net) + VAT. The Settled tab shows the receipt, and Reprint receipt prints a COPY.
- [ ] Use SJK3ABC123 again on another ticket. It's refused with the ticket and time it was used on.
- [ ] Try to settle a ticket with a pending void request or with unprinted items. It's refused with a plain message, and "Print, then settle" works.
- [ ] Leave a ticket part paid. Its tile shows "Part paid · KSh 370 left", and `close-day` refuses with that ticket listed.
- [ ] A waiter can't settle, even with a supervisor's PIN.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/08-settle` and stop.

When done, report as `AGENTS.md` describes, with what I should test on the real Windows PC, printer and cash drawer, and anything you decided differently from this prompt.
