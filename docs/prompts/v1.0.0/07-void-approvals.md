# Task 7 — Voids, gifts, discounts, price changes and approvals

Read `AGENTS.md` first. Use the wireframes **"5 · Void request"** and **"6 · Supervisor approvals"** in `docs/wireframes/`. Work on a new branch `screen/07-void-approvals` from `main` (tag `screen-06-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This task turns on the disabled **Void** and **Gift** buttons from Task 6. It adds discounts and on-ticket price changes, and builds the full approval system:
- the **supervisor's PIN on the spot** (Option 1)
- a **request** that the supervisor approves from **their own counter** (Option 2)

The PIN pop-up from Task 5 is reused and extended, not replaced.

**Not in this task:**
- **Settling** comes in Task 8, but it must already refuse a ticket that has pending requests (Part B).
- **Stock** for voided or gifted items comes in Task 11. Record what Task 11 will need (Part C).
- **The admin Permissions screen** comes in Task 10. Set the defaults in the seed and migration.

---

## Part A — Approval requests (domain and API)

**`ApprovalRequest`:**
- kind: `Void`, `Gift`, `Discount`, `PriceChange`, `Reprint`, `Move`, `Merge`
- ticket and line (where it applies)
- payload (JSON): the quantity, reason, amount, target table and so on
- requested by, requested at, and the terminal
- preferred approver (optional)
- status: `Pending`, `Approved`, `Rejected`, `Cancelled`, `Expired`
- decided by, decided at, and a rejection note (optional)
- row version

**Who can approve:** a request can be approved by any user whose permission for that action is **Allowed**, and **never by the requester**. That applies to the PIN and to requests alike.

**How a request is decided:**
- **Approving** runs the action **on the server, in the same transaction**, exactly as if the requester had done it with approval. The record keeps both the requester and the approver.
- Re-check everything at approval time. If the ticket or line changed so that the action no longer fits (the line is already voided, the ticket is settled or merged), the approval fails with a plain message and the request becomes `Expired`.
- **Rejecting** changes nothing on the ticket. The requester sees the result live.
- **The requester can cancel** their own pending request.
- **Expiry:** pending requests expire after `pos.approvalExpiryMinutes` (default 30), and when their ticket closes or merges.

**Where requests show:** a request goes to **every logged-in user who can approve it**. The preferred approver sees it at the top, marked "Sent to you". Requests never get stuck because one supervisor logged out.

**Endpoints:**
- `POST /api/pos/approvals`
- `GET /api/pos/approvals?status=pending`: the ones I can approve, plus my own
- `GET /api/pos/approvals/approvers?kind=…`: the logged-in users who can approve this kind, with their terminal, for the "Send request to" list
- `POST /api/pos/approvals/{id}/approve`
- `POST /api/pos/approvals/{id}/reject`
- `POST /api/pos/approvals/{id}/cancel`

Every write takes `Idempotency-Key`, and every decision is audited.

**Events:**
- `approval.requested` goes to approvers.
- `approval.resolved` goes to the requester and approvers.
- `ticket.changed` is published as usual.

**The PIN path stays as it is** (single-use token, 60 seconds), with three changes:
1. The PIN user must be Allowed for that exact action.
2. The PIN user must not be the requester.
3. The approver is recorded on the action.

**Users who are Allowed** do the action directly, with no PIN and no request. It's still audited.

## Part B — Void

**Applies to:** **Printed** lines only. New lines are cancelled, as in Task 6.

**Void pop-up** (match wireframe 5).

Left side:
- "Void 1 × Guiness" and "Table 7 · #1042 · sent 19:42 by edna"
- **Quantity to void**, from 1 up to the line quantity, with − and +
- **Reason**, required:
  - Wrong item
  - Customer changed mind
  - Spilled or broken
  - Other, which requires a note
- the "Once approved" box from the wireframe
- **Cancel**

Right side:
- **For a user with `order.void` Allowed:** a single **Void** button. No PIN is needed.
- **For a user with `order.void` NeedsApproval:**
  - **Option 1 · supervisor is here:** the PIN pad and **Approve**.
  - **Option 2 · supervisor elsewhere:** **Send request to**, listing the approvers with their counter, and **Send void request**.
- **For a user with `order.void` NotAllowed:** the Void button is hidden.

**What a void does** (server, one transaction, with the ticket row locked):
1. **Partial voids:** if the quantity is less than the line's, split the line. The voided part becomes a new line with the same added-by, terminal, time, price and print round, and status `Voided`.
2. Record `VoidLog`:
    - line, quantity, amount
    - reason and note
    - requester and approver
    - terminal and time
    - **"was poured" as a yes/no**: "Spilled or broken" means yes. For the other reasons, the void pop-up asks "Was it already poured?" (Yes / No), defaulting to No. Task 11 uses this for stock.
3. **VOID slip** to the **dispense printer that printed the original line**. Store that printer on each print round, if it isn't stored already. The slip shows:
    - "VOID" in large text, with the dispense header
    - table, ticket and time
    - quantity × short name and modifiers
    - the reason
    - "By: edna · Approved: MAU"
4. **Corrected bill:** if the ticket has had a bill printed, a corrected bill prints automatically to the requesting terminal's receipt printer:
    - with the next bill number
    - "Corrected bill · replaces earlier bills"
    - with the voided items left off
5. The voided items drop out of the ticket total and out of sales.

**On the ticket:**
- A **voided** line stays visible, struck through, with a **Voided** badge and the reason. It doesn't count towards the total, and nothing can be done to it.
- A line with a **pending** request shows **"Void pending"** and can't be voided, gifted or changed again until the request is decided.

**Settling:** a ticket with any pending request can't be settled. Put that check where Task 8 will call it, and add a test.

## Part C — Gift

- **Applies to:** any New or Printed line that isn't voided. The pop-up has the same layout as Void: quantity, then a reason (Regular customer / House / Compensation / Other with a note), then the same approval options.
- **Permission:** `order.gift`.
- **What it does:**
  - It splits the line for a partial quantity, as void does.
  - The gifted part keeps its **original price** for reports, but its amount and VAT become **0**. It's marked `IsGift`.
  - It writes `GiftLog` with the same fields as VoidLog, apart from "was poured". Gifted items count as sold for stock in Task 11.
- **On the ticket:** a **Gift** badge, with the amount shown as "0" and the original price struck through.
- **On the bill:** "GIFT" next to the item, with the amount 0.
- If a bill was already printed, a **corrected bill** prints, as in Part B.

## Part D — Discount (ticket level)

- **Button:** a **Discount** button on the order screen, next to Ticket note.
- **Pop-up:**
  - **Percent** or **Amount (KSh)**
  - a reason (Regular customer / Promotion / Compensation / Other with a note)
  - the approval options
- **Permission:** `order.discount`.
- **Limits:** one discount per ticket. A new one replaces the old one, and removing it also needs the permission. The discount can't be more than the total of the non-gift, non-voided lines, and percent is limited to 100.

**Spreading the discount across lines:** spread it in proportion to each line's amount, so VAT stays correct per rate.
- Round each line's share to the currency's smallest unit.
- Put the leftover from rounding on the largest line.
- Store each line's share, because the ledger (Task 8) and ERPNext need them.
- Re-spread the discount whenever lines change.

**Bill:** Subtotal, then "Discount 10% (Regular customer)" as a negative amount, then TOTAL. The VAT lines are worked out on the **amounts after the discount**.

Write `DiscountLog`. If a bill was already printed, a **corrected bill** prints.

## Part E — Change a price on a ticket

- **Applies to:** a selected line that isn't voided or gifted, through **Change price**, needing `order.change_price`. **This is not `menu.edit_prices`**, which edits the menu.
- **Pop-up:**
  - the current price
  - the new unit price, on a number pad
  - a reason, required
  - the approval options
- **What it does:**
  - It changes the line's unit price and amount, and keeps the original in `OriginalUnitPrice`.
  - It writes to `PriceChangeLog` with kind `Ticket`, recording the line, old, new, who and the approver.
  - Partial quantities split the line.
- **Display:** the ticket shows the new price with "Price changed" under it. The bill shows the new price only.
- If a bill was already printed, a **corrected bill** prints.

## Part F — Requests for reprint, move and merge

The Task 5 and Task 6 PIN pop-up becomes the **shared approval component**, used by every action above. Give it **Option 2 (send request)** as well, so reprint, move and merge can be requested from another counter too. When one of those is approved, the server performs it:
- **Reprint:** the bill prints on the **requester's** terminal.
- **Move:** the ticket moves to the requested table.
- **Merge:** the requested tickets are merged.

## Part G — The approvals screen (supervisor's counter)

Match wireframe 6.

**Top bar button:** on the table map and the order screen, users who can approve anything see an **Approvals** button with a count badge ("Approvals 2").

**When a request arrives:**
- a short **sound** (respect the browser's autoplay rules, and play it from the first interaction onwards)
- a toast: "edna asks: Void 1 × Guiness · Table 7. Open"
- the badge goes up

**Screen `/pos/approvals`:**
- **Waiting for you** cards, each with:
  - what's asked and the amount
  - "Table 7 · #1042 · requested by edna at Pillar 1 · 1 min ago", updating live
  - the reason and note
  - **Reject**, with an optional note
  - **Approve void**, or Approve gift / discount / price / reprint / move / merge
- **Earlier this shift:** decided requests for the current work period, with **Approved** or **Rejected** tags.
- **Text from the wireframe:** keep the line "The supervisor can also open any table and void directly…". That's already true through Allowed permissions.

**The requester:**
- Their line shows "Void pending" or "Gift pending", or the ticket shows "Discount pending" and so on.
- When the request is decided, a toast appears: "MAU approved the void" or "MAU rejected: [note]".
- A **Cancel request** link sits on the pending line.

**Idle logout:** the Approvals screen follows the idle logout like every other screen.

## Part H — Default permissions

Set these where they aren't set yet, following the design's grid:

| Permission | Waiter | Cashier | Supervisor | Manager |
| --- | --- | --- | --- | --- |
| `order.void` | NeedsApproval | NeedsApproval | Allowed | Allowed |
| `order.gift` | NotAllowed | NeedsApproval | Allowed | Allowed |
| `order.discount` | NotAllowed | NeedsApproval | Allowed | Allowed |
| `order.change_price` | NotAllowed | NotAllowed | NeedsApproval | Allowed |

`pos.approvalExpiryMinutes` defaults to 30.

## Part I — Tests

**Server:**
- **Approvals:**
  - Every kind, through all three paths: Allowed directly, with a PIN, and as a request.
  - The requester can never approve their own request, whether by PIN or from the list.
  - Someone without Allowed can't approve.
  - The approver list only includes logged-in users who can approve that kind.
  - Expiry after the setting's minutes (fake clock), and when the ticket is merged.
  - Approving re-checks state: a line voided in the meantime makes the request `Expired`.
  - Two supervisors approving the same request at the same time: exactly one wins, on both providers.
  - Cancel and reject change nothing on the ticket.
- **Void:**
  - full and partial voids, with the split line keeping added-by, terminal and print round
  - the VOID slip goes to the original dispense printer
  - a corrected bill prints only if a bill was printed before
  - totals and VAT are correct after the void
  - VoidLog fields, including "was poured"
  - printed lines only
  - a line with a pending request is blocked
- **Gift:** the amount and VAT are 0, the original price is kept, and the bill shows "GIFT".
- **Discount:**
  - percent and amount
  - spreading across mixed VAT rates, with the rounding leftover on the largest line and the shares adding up exactly to the discount
  - it can't exceed the total
  - it's re-spread after a line is added or voided
  - VAT is worked out after the discount
- **Price change:**
  - the original price is kept
  - it's written to PriceChangeLog as kind Ticket
  - `order.change_price` and `menu.edit_prices` are separate
- **Settling:** the check refuses a ticket with pending requests.
- **Golden slips:** the VOID slip, a corrected bill with a void, a gift and a discount, at 42 and 32 columns.

**Web:**
- **Void pop-up:** the quantity limits, an "Other" reason needing a note, and the three permission states.
- **Option 2:** the request shows "Void pending", then updates live when approved or rejected.
- **Approvals screen:**
  - the badge count and the toast
  - approve and reject
  - "Earlier this shift"
- **Gift, discount and price-change pop-ups:** they work and update the totals.
- **Shared approval component:** reprint, move and merge through Option 2.

## Acceptance checklist

- [ ] As edna on Pillar 1, void 1 of 2 printed Tusker with the reason "Wrong item", choosing Option 2 to MAU at Counter B. Counter B shows the toast, the sound and the badge, and MAU approves.
    - edna's screen updates live, with one line Printed and one Voided.
    - A VOID slip prints on the dispense printer that printed the Tusker.
    - A corrected bill prints on Pillar 1's receipt printer.
    - The total drops by 250.
- [ ] The same void with Option 1 (MAU's PIN on Pillar 1) works. Using edna's own PIN is refused.
- [ ] As MAU, void directly with no PIN. It's logged with MAU as both requester and approver.
- [ ] Gift 1 Guiness: the bill shows "GIFT" with 0, and the original 300 is kept in the log.
- [ ] A 10% discount on a ticket with 16% and exempt items: the bill shows subtotal, discount and total, and the VAT lines are worked out after the discount.
- [ ] A waiter can't see Change price. A supervisor's change asks for a manager's approval.
- [ ] A pending request blocks the settle check, and it expires after `pos.approvalExpiryMinutes`.
- [ ] Reprint as edna using Option 2: MAU approves on Counter B, and the bill prints on edna's terminal.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/07-void-approvals` and stop.

When done, report as `AGENTS.md` describes, with what I should test on the real Windows PCs and printers, and anything you decided differently from this prompt.
