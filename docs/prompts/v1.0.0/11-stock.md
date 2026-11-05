# Task 11 — Stock: items, recipes, deliveries, waste, transfers and counts

Read `AGENTS.md` first. Use the wireframes **"11 · Admin: stock and recipes"**, **"12 · Admin: receive a delivery"** and **"13 · Admin: stock count and variance"** in `docs/wireframes/`. Work on a new branch `screen/11-stock` from `main` (tag `screen-10-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

Stock is Mau's main loss control. Every sale must reduce stock through its recipe. Deliveries, waste and transfers must move it. A **count** compares what should be there with what is there, and posts the difference to the books as stock loss.

**Not in this task:**
- Stock reports (on hand, variance, purchases) come in Task 13. This task only builds the on-hand list on the stock page.
- Paying suppliers later, from the cash book, comes in Task 12. This task records what's owed.

---

## Part 0 — Carry-overs

1. **Flaky test.** Make `CheckConnectionTests` use a local TCP listener that accepts nothing and never answers, instead of 192.0.2.1, so it doesn't depend on the network.
2. **"Not served" on approval.** When `stock.returnOnVoidIfNotServed` is on, the **approver** can confirm or change "Was it already poured?" when approving a void, both on the approvals screen and with the PIN. The approver's answer is what's stored.
3. **Voids on bills.** The owner decided bills keep leaving voided items off. Record it in DECISIONS.md.

## Part A — Stock model

**Stock location:** name, active. Seed **Store** and **Bar**.
- Each **terminal** gets a **Sells from** location (default Bar), set on the Terminals page from Task 4.
- Sales deduct stock from the location of the terminal that **printed** the line.

**Stock item:**
- name
- category (Beer, Spirits, Soft drinks, Food, Other)
- **base unit**: `bottle`, `can`, `piece`, `ml`, `g`
- **pack units**: a list of a name and a factor in base units. For example, Tusker has base unit bottle and the pack "crate" = 25. Viceroy has base unit ml, with the packs "bottle" = 375 and "carton" = 9,000 (24 × 375).
- reorder level, in base units, per location (optional)
- **average cost** per base unit
- active

**Stock movement:** the only source of truth for quantities.
- item
- location
- quantity in base units (+ in, − out)
- unit cost at that moment
- kind: `Opening`, `Delivery`, `DeliveryReversal`, `Sale`, `VoidReturn`, `Waste`, `TransferOut`, `TransferIn`, `CountAdjust`
- reference (line, delivery, count and so on)
- user
- time
- work period

**On hand** = the sum of movements per item and location. Keep a cached on-hand table, updated **in the same transaction** as each movement, and add a test that it always equals the sum.

**Negative stock is allowed,** so a sale is never blocked by stock. The stock page shows negative quantities in orange, with "Below zero: check deliveries or the last count".

**Average cost:** weighted average, recalculated on each delivery:

`(on hand × average cost + delivered quantity × delivered unit cost) ÷ (on hand + delivered quantity)`

If on hand is 0 or less, the new average is the delivered cost. Store the cost on every movement.

**Quantities:** use `decimal` throughout. Show quantities in the clearest pack, for example "3 crates 7 bottles" or "2.4 bottles (900 ml)".

## Part B — Recipes and selling

**Recipe:** a menu item has a list of a stock item, a quantity and a unit (any unit of that stock item). For example, Viceroy Tot = 25 ml of Viceroy 375ml, and Tusker Lager = 1 bottle of Tusker.

**One recipe per menu item.** Modifier options don't change the recipe. Option-level recipes are a follow-up.

**When stock moves:**
- **Printing:** when a line becomes **Printed** (Task 6), the server posts a `Sale` movement for each recipe ingredient × the line's quantity, from the printing terminal's **Sells from** location, at the current average cost.
  - **Cost of sales:** in the same transaction, post a journal entry: debit **Cost of sales**, credit **Stock**, for the total cost of that print round. Use one entry per print round.
- **Cancelled New lines** never moved stock, so there's nothing to reverse.
- **Voids:**
  - If the void is **not poured** and `stock.returnOnVoidIfNotServed` is on, post a `VoidReturn` that puts the stock back at the cost of the original movement, and reverse its cost of sales.
  - If it **was poured**, the stock stays out, and the journal entry moves its cost from Cost of sales to **Stock loss**, so voided pours show as loss, not sales cost.
- **Gifts** count as sold: they stay in Cost of sales. The Gifts report in Task 13 shows them.
- **Merging and moving tickets** don't move stock.
- **No recipe:** a menu item without a recipe moves no stock. The stock page shows a warning, "12 menu items have no recipe, so they don't reduce stock", with a link to the list.

**Existing printed lines:** printed lines from before this task are **not** back-filled. Opening stock (Part F) starts the count fresh.

## Part C — Stock page (`/admin/stock`), wireframe 11

Access needs any `stock.*` permission. Editing items and recipes needs `menu.edit`.

**Top:**
- a location filter (All / Store / Bar)
- search
- the buttons **Record waste**, **Transfer**, **Add stock item**

**Table:**
- stock item
- unit (in base units, with its packs)
- **on hand** per the selected location, or the total
- reorder level
- average cost
- value (on hand × average cost)

Rows below their reorder level are highlighted in orange, as in the wireframe, with "Orange = below reorder level". Tapping a row opens the item's **movement history**: date, kind, quantity, location, user and reference, filterable by date.

**Right panel, "Recipe":**
- choose a menu item
- one ingredient row each: quantity, unit and stock item
- **+ Add another ingredient**
- **Cost per sale**, **Price** (from the Standard price list) and **Margin %**
- **Save recipe**, which is audited

**Add or edit stock item:** the Part A fields, with a pack units editor. An item that has movements can't change its base unit. Say why.

## Part D — Deliveries (`/admin/deliveries`), wireframe 12

**Suppliers:** a simple list of name, phone, KRA PIN (optional), notes and active. It's reached from **Manage suppliers** on this page.

**Receive a delivery:**
- **Header:**
  - supplier
  - supplier invoice number, **unique per supplier**: "Invoice 4471 from KBL is already recorded on 12 Oct"
  - date, defaulting to today and never in the future
  - **Into** a location
- **Lines:**
  - stock item
  - **pack unit**, defaulting to the item's largest pack
  - quantity
  - **unit cost per that pack**
  - line total
  - remove

  **+ Add item**. The cost defaults to the last cost paid to this supplier for this item and pack.
- **Payment:**
  - **On credit** adds to what's owed to that supplier.
  - **Paid cash (from the safe).** Paying from a cashier's drawer goes through Pay out in Task 9, so say so in the help text.
  - **Paid M-Pesa / bank:** choose M-Pesa or Bank, plus a reference.
- **Delivery total**, and **Save delivery**.

**On save** (one transaction):
1. `Delivery` movements in base units (quantity × the pack factor), at a unit cost of the pack cost ÷ the pack factor.
2. The average cost is updated.
3. **Journal:** debit **Stock**, credit **Supplier payables** (on credit), **Safe**, **M-Pesa** or **Bank**.
4. Audit it.

**Deliveries list:** date, supplier, invoice, location, total, payment, and who recorded it. Tapping one opens it read-only, with **Reverse delivery**. A reversal needs a reason and `stock.adjust`. It posts `DeliveryReversal` movements at the delivery's cost and a reversing journal entry, and recalculates the average cost. It's refused if it would take on hand below 0, with the message "Some of this delivery has already been sold. Count the stock instead."

**Supplier balance:** each supplier shows **owed** (credit deliveries − reversals). Payments to suppliers come in Task 12.

## Part E — Waste and transfers

**Record waste:**
- location, stock item, quantity and unit
- reason: Broken, Expired, Spilled, Staff use, Other (with a note)
- It posts a `Waste` movement at the average cost.
- **Journal:** debit **Stock loss**, credit **Stock**.
- Permission: `stock.waste`.

**Transfer:**
- from location, to location, and lines of a stock item, quantity and unit
- It posts `TransferOut` and `TransferIn` at the average cost. Average cost is business-wide, so there's no journal entry.
- Permission: `stock.receive`.
- It prints a **transfer slip** on the admin's chosen printer (optional).

**Both** are audited, and listed on a **Stock activity** tab (waste and transfers, filterable).

## Part F — Stock counts (`/admin/stock-counts`), wireframe 13

**A count is for one location.** Its kind is **Full** (every active item at that location) or **Partial** (picked items).

**As-at time:** a count has an **as-at time**, set when it's started. The expected quantity is the on hand **at the as-at time**.
- Recommend counting after closing the day. If the day is open, show a note: "Sales after [as-at time] are still recorded. The adjustment only corrects stock up to the as-at time."
- At approval, the adjustment is **counted − expected at the as-at time**, so sales made during the count are kept.

**Statuses:** `Draft`, `Submitted`, `Approved`, `Rejected`.

**Draft:**
- **Print count sheet** prints an A4 PDF from the admin site. Optionally it prints on a receipt printer too. It lists the items **without the expected figures**, with blank boxes per pack, so counting is blind.
- **Entering counts:** each item takes figures per pack unit and base unit, for example "3 crates + 7 bottles", or "2 bottles + 150 ml" for spirits. The total is shown in base units.
- **Before submitting, the expected and difference columns are hidden** (blind count), the same as cash-up. Uncounted items in a Full count block submission: "7 items not counted. Enter 0 if there are none."

**Submit for approval** needs `stock.count`. After submitting, the counter sees Expected, Counted, Difference (in orange when it's not 0) and Value. At the bottom:
- **Items short**
- **Total variance** in KSh
- the note: "Approving the count sets stock to the counted figures and posts the variance to the books as stock loss."

**Approve** needs `stock.adjust` Allowed, and the approver can't be the person who counted.
1. `CountAdjust` movements, at the average cost.
2. **Journal:**
    - **Short:** debit **Stock loss**, credit **Stock**.
    - **Over:** debit **Stock**, credit **Stock loss**.
3. The count is locked. **Reject** returns it to Draft, with a note.

**Opening stock:** the first count of a location may be marked **Opening stock**.
- There's no expected figure.
- Each line also takes a **unit cost**, which sets the average cost.
- On approval it posts `Opening` movements, with a journal entry: debit **Stock**, credit **Opening balances**. Add that account if it's missing.
- The setup wizard (Task 14) will reuse this.

**Counts list:** date, location, kind, counted by, approved by, status, and the variance in KSh.

## Part G — Approvals on the admin site (stock only)

`stock.*` keys can be **Needs approval** (Task 10). The admin site has no PIN pad, so for stock documents, "Needs approval" means:
- The document is **saved as Pending**. This applies to a delivery, waste, transfer or reversal.
- It shows in an **Awaiting approval** list at the top of the Stock page.
- A user **Allowed** for that key (not the creator) approves it. Only then do the movements and journal entries post.
- It can be rejected, with a note.

Counts already work this way. Audit every step, and publish `stock.changed`.

## Part H — Default permissions and seed

**Permissions** (set only where they aren't set yet), following the design grid:

| Permission | Waiter | Cashier | Supervisor | Manager |
| --- | --- | --- | --- | --- |
| `stock.receive` | NotAllowed | NotAllowed | Allowed | Allowed |
| `stock.waste` | NotAllowed | NotAllowed | Allowed | Allowed |
| `stock.count` | NotAllowed | NotAllowed | Allowed | Allowed |
| `stock.adjust` | NotAllowed | NotAllowed | NeedsApproval | Allowed |

**Seed `--demo`:**
- the Store and Bar locations, with every terminal selling from Bar
- the stock items for the demo menu, with packs: beers as bottle with crate = 25, spirits as ml with bottle and carton, sodas as bottle with crate = 24, and so on
- the recipes for every demo menu item
- a demo supplier, KBL
- an **approved opening count** for Store and Bar, so on hand and values show straight away

## Part I — Tests

**Server:**
- **On hand:**
  - the cached on hand always equals the sum of movements, checked as a property-style test over random sequences
  - negative stock is allowed and flagged
- **Average cost:** a delivery onto a positive, zero and negative on hand. Pack conversions give the right base quantities and unit costs.
- **Selling:**
  - Printing posts `Sale` movements from the printing terminal's location, with Cost of sales and Stock in one balanced entry per print round.
  - A recipe in ml deducts the right fraction of a bottle.
- **Voids:**
  - not poured with the setting on gives a `VoidReturn` and a reversal of cost of sales
  - poured moves the cost to Stock loss
  - with the setting off, every void is poured
  - the approver's answer overrides the requester's
- **Gifts:** they stay in Cost of sales.
- **No recipe:** no movement, and the item appears in the warning list.
- **Deliveries:**
  - the invoice number is unique per supplier
  - no future dates
  - the journal entry for each payment type
  - the reversal journal entry, and a reversal that would go below 0 is refused
  - the last cost is suggested
- **Waste and transfer:** their movements and journal entries, and a transfer leaves the total stock unchanged.
- **Counts:**
  - blind until submitted (the API hides expected)
  - a Full count needs every item
  - the adjustment uses the as-at expected, with sales during the count kept
  - short and over journal entries
  - the counter can't approve their own count
  - an opening count sets the cost and posts to Opening balances
- **Stock Needs approval on the admin site:** documents stay Pending, nothing posts until an Allowed user who isn't the creator approves, and rejecting posts nothing.
- **Every journal entry** in this task balances.
- **Concurrent prints** of two tickets with the same item: on hand stays consistent, on both providers.
- **Count sheet PDF:** generated, with no expected figures in it.

**Web:**
- **Stock page:**
  - location filter, orange low rows, negative rows
  - movement history
  - the no-recipe warning
- **Recipe editor:** units per item, and the cost, margin and price.
- **Delivery form:**
  - pack defaults and the last cost
  - the invoice number check
  - payment options and the total
- **Waste and transfer forms.**
- **Count:**
  - blind entry
  - packs plus base units totalling correctly
  - the uncounted block
  - after submitting, the variance colours
  - approve and reject
- **Awaiting approval list.**

## Acceptance checklist

- [ ] After `seed --demo`, the Stock page shows Store and Bar quantities and values from the opening counts.
- [ ] As edna, print 2 Tusker and 1 Viceroy Tot on Counter A. Bar on hand drops by 2 bottles of Tusker and 25 ml of Viceroy, and a balanced Cost of sales entry exists for that print round.
- [ ] Void 1 Tusker as not poured: Bar Tusker goes back up by 1. Void a Viceroy Tot as poured: the stock stays out, and its cost moves to Stock loss.
- [ ] Receive a delivery from KBL of 4 crates of Tusker at 2,500 per crate on credit, into Store. Store goes up by 100 bottles, the average cost updates, and KBL shows 10,000 owed. Entering the same invoice number again is refused.
- [ ] Transfer 2 crates of Tusker from Store to Bar. Bar goes up by 50 and Store down by 50.
- [ ] Record 1 broken Guiness. Stock loss goes up by its cost.
- [ ] Start a Full count of Bar. The count sheet has no expected figures. Enter the counts with Tusker 3 bottles short, and submit. The variance shows −3 and its value in KSh. MAU can't approve his own count. The Administrator approves, the stock is set to the counted figures, and Stock loss is posted.
- [ ] With a supervisor's `stock.adjust` set to Needs approval, a delivery reversal by MAU waits in Awaiting approval until the Administrator approves it.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/11-stock` and stop.

When done, report as `AGENTS.md` describes, with anything you decided differently from this prompt.
