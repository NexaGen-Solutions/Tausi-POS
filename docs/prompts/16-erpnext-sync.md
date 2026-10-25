# Task 16 — ERPNext sync (optional module)

Read `AGENTS.md` first. Use the wireframe **"20 · Admin: ERPNext sync (optional)"** and the **"Optional: ERPNext integration"** section of `docs/design.md`. Work on a new branch `screen/16-erpnext-sync` from `main` (tag `release-15-v1`, or the latest tag on main).

Do not merge into `main` and do not create tags. Push your branch and stop.

Tausi POS works fully on its own. This task adds the **optional ERPNext Sync module**:
- **ERPNext → POS:** items, item groups and prices, on demand or nightly
- **POS → ERPNext:** settled sales, either one invoice per ticket or one summary invoice per day

The sync is **one-way per direction** and runs **in the background**, so ERPNext being slow or offline **never** affects ordering, printing or settling.

**Out of scope for v1:**
- Stock movements, deliveries, expenses and journal entries to ERPNext. The design only lists sales and the menu. Record these as follow-ups (Part H). Keep the code organised so a new document type is easy to add.
- Two-way sync, and ERPNext editing POS data, apart from the menu pull.

---

## Part A — Module, settings and connection

**Off by default:**
- The module is off by default (`erp.enabled` = false).
- The **ERPNext sync** admin page needs `erp.manage`.
- When the module is off, nothing runs: no worker, no queue rows. The page shows only the switch and the connection form.

**Settings** (in the Task 10 registry, section "ERPNext"):
- `erp.url`, HTTPS only (HTTP is allowed in Development)
- `erp.apiKey`
- `erp.apiSecret`, **stored encrypted** with the data-protection keys and never returned by any API (the page shows `••••` and **Change**)
- `erp.company`
- `erp.salesMode`: `PerTicket` (default) or `DailySummary`
- `erp.invoiceDoctype`: `Sales Invoice` (default) or `POS Invoice`
- `erp.customer`: the walk-in customer, default "Walk-in Customer"
- `erp.posProfile`, needed for POS Invoice
- `erp.costCenter` (optional)
- `erp.warehouse` (optional, passed on items)
- `erp.submitInvoices`: create and **submit** (default), or leave as Draft
- `erp.pullSchedule`: Off / Nightly at a set time
- `erp.timeoutSeconds`: 20

**Test connection:**
1. Calls `GET /api/method/frappe.auth.get_logged_user` with the token header `Authorization: token <key>:<secret>`.
2. Checks that the **company**, **customer**, **modes of payment**, **POS profile** (if used) and the **income/tax accounts** exist.
3. Shows each check with ✓ or a plain error, such as "Mode of Payment 'M-Pesa' not found in ERPNext. Create it or change the mapping."

**HTTP client:** use a typed client in Infrastructure (`IErpNextClient`), with:
- the timeout
- **retries with backoff** for timeouts, 5xx and 429
- no retry for 4xx validation errors, which are reported to the admin
- logging of requests (without secrets) to the sync log

## Part B — Mapping

**Payment types → Modes of Payment:**
- Cash → "Cash", M-Pesa → "M-Pesa", Card → "Credit Card".
- Each can be edited, using a dropdown filled from ERPNext.

**VAT → Sales Taxes and Charges Template:**
- Each POS VAT rate maps to an ERPNext **tax template** or tax account, filled from ERPNext.
- Exempt and zero-rated map separately.
- Prices **include VAT**, so taxes are sent with `included_in_print_rate = 1`.

**Items:** each menu item uses its **ERPNext item code** (the field from Task 3).
- **Menu → Items needing a code** lists the items without one.
- **Auto-match by name** suggests codes from ERPNext items with the same name, which the owner confirms.

**Extra charges:**
- **Discounts** are sent as each line's discount (its discount share from Task 7), so totals and VAT match exactly.
- **Rounding** goes to a configured rounding account, or as the ERPNext `rounding_adjustment`. Choose whichever gives an exact match, and document it.
- **Gifts** are sent at the original price with a 100% line discount, so ERPNext stock and sales quantities stay right.
- **Voided and cancelled** lines are left out.

## Part C — POS → ERPNext: sales

**ErpSync table:**
- kind: `Invoice` / `DailySummary` / `MenuPull`
- POS reference (ticket id or business date)
- status: `Pending`, `Sending`, `Sent`, `Failed`, `Skipped`
- attempts
- next attempt at
- ERPNext document name
- last error
- payload hash
- created and updated

**PerTicket mode:**
- **On settle**, in the **same transaction** as the settle, insert a `Pending` row for the ticket. This is the outbox pattern, so a crash never loses a sale and settling never calls ERPNext.
- A **background worker** (a hosted service) picks up rows in order, **one at a time per business**, and builds the invoice:
  - customer, company, `posting_date` = the ticket's **business date** (Task 12), and `posting_time`
  - `is_pos = 1`, with the **payments** from the mapping
  - items: item code, qty, rate (gross, VAT-inclusive), and the line discount
  - taxes, with the VAT included
  - cost center and warehouse, if set
  - `update_stock` from a setting, default **off**, because ERPNext stock isn't kept in sync in v1. Record why in DECISIONS.md.
  - **remarks**, which hold the POS ticket and receipt numbers

**No duplicates:**
- Each invoice carries the POS **receipt number** in a **unique field**. Use a custom field `tausi_receipt_no` (Data, unique) on the invoice doctype. **Test connection** checks that it exists, and the docs explain how to add it.
- Before creating an invoice, **look it up** by that field. If it's found, mark the row `Sent` with that document name.
- A retry can therefore never create a second invoice.

**Totals must match:** after creating the invoice, compare ERPNext's `grand_total` with the POS total. A difference over 0.01 marks the row `Failed`, with "Totals differ: POS 1,370.00, ERPNext 1,369.60", and **cancels nothing**. The admin decides.

**Failures:**
- Missing item code → `Failed`, with "Item 'Pool Table' has no ERPNext item code", as in the wireframe.
- A network error or a 5xx → back to `Pending`, with backoff (1, 2, 5, 15 and 60 minutes, then hourly).
- After **10 attempts** → `Failed`.

**Retrying:** **Retry failed** sends every Failed row back to Pending. **Retry** and **Skip** (with a reason, audited) are also available per row.

**DailySummary mode:**
- At **Close day** (Task 9), insert one `DailySummary` row for that business date.
- It sends one invoice to ERPNext with **items aggregated** (item code, total qty, total amount, total discount), payments by mode, and the taxes.
- It's matched by `tausi_receipt_no` = `DAY-<business date>`, so it's never sent twice.
- **Late entries** after the close don't change a sent summary. They appear as an adjustment row on the next day's summary, and the page notes it.

**Switching mode** only applies to business dates that **aren't sent yet**, and the page warns about it.

## Part D — ERPNext → POS: menu and prices

**Pull menu and prices** (button, or nightly):
1. **Item Groups** → categories (matched by name).
2. **Items** that are **sales items and not disabled** → menu items (matched by **item code**), with name, short name (the item name cut to 18 characters, editable), category and VAT (from the item's tax template, when it maps).
3. **Item Prices** for the selling price lists the owner chooses → Tausi price lists (for example ERPNext "Standard Selling" → Standard).

**Preview first:** the same flow as the Task 14 import, listing new items, changed prices, changed names, and items no longer in ERPNext (these are marked **inactive**, never deleted). Nothing changes until **Apply**.

**What a pull leaves alone:**
- Tausi **keeps its own** modifiers, recipes, short-name edits, tables and screen layout. A pull never changes them.
- **Price changes** from a pull are logged in PriceChangeLog with the user "ERPNext sync", and publish `menu.changed`. Lines already on tickets keep their copied prices (Task 6).
- **Nightly pulls** apply automatically **only** when `erp.pullAutoApply` is on (default off). Otherwise they prepare a preview, and show a banner: "ERPNext has 14 menu changes waiting for review".

## Part E — The ERPNext sync page (wireframe 20)

Match the wireframe:
- **Header:** "ERPNext sync", "Optional. The POS works fully without it.", and the **Sync on** switch.
- **Left, Connection:**
  - ERPNext address, API key, API secret
  - **Send sales as**: One invoice per ticket / One summary invoice per day
  - **Test connection**, **Pull menu and prices**
  - links to **Mappings** (payment modes, taxes, price lists, other settings) and **Items needing a code**
- **Right:**
  - **Tiles:** **Waiting**, **Sent today** and **Failed** counts, updating live from `erp.sync.changed`.
  - **Recent syncs**, with **Retry failed**: ticket, status pill (Sent / Waiting / Failed), ERPNext invoice (a link that opens the document in ERPNext in a new tab), and note.
  - **Filters:** status, date and kind.
  - Under it, the wireframe's note: "Sales go up a few seconds after settle; failed ones retry automatically. Each POS product stores its ERPNext item code."

**Banner elsewhere:** if anything has been Failed for more than 24 hours, a banner shows in the admin site for users with `erp.manage`: "3 sales haven't reached ERPNext".

## Part F — Safety and performance

- **Service never waits for ERPNext.** Settle, print and close day must never wait for ERPNext. Add a test with ERPNext returning after 30 seconds: settle still completes in under 500 ms.
- **The worker** keeps going after a server restart, rows stuck in `Sending` for over 5 minutes go back to Pending, and it pauses itself when the module is turned off.
- **Rate:** at most 2 requests per second to ERPNext (a setting), so it doesn't overload a small ERPNext server.
- **Privacy:** no PINs, user details or ledger internals are sent. Each invoice carries the waiter's and cashier's **names** in remarks, and only when `erp.sendStaffNames` is on (default on).
- **The ERPNext secret** never appears in logs, exports, backups shown in the UI, or API responses.

## Part G — Tests

**A fake ERPNext:** build a small fake ERPNext HTTP server in the integration tests, using WireMock.Net or a minimal ASP.NET test host. It covers:
- auth
- resource GET/POST
- the unique `tausi_receipt_no` check
- submit
- configurable errors and delays

Add the library to THIRD-PARTY-NOTICES if it ships. It shouldn't, since it's only used in tests.

**Server:**
- **Test connection:** each check passes and fails with the right message, and the secret is never returned.
- **Outbox:** a settle writes the Pending row in the same transaction, a failed settle writes none, and a crash between settle and send still sends later.
- **The invoice payload:**
  - items, rates, line discounts and gifts (100% discount)
  - VAT included per rate, exempt and zero-rated
  - rounding
  - mixed payments mapped to modes
  - the business date past midnight
  - ERPNext's `grand_total` equal to the POS total in every case, against the fake's calculation
- **No duplicates:** a retry after a timeout where ERPNext **did** create the invoice finds it by `tausi_receipt_no` and doesn't create a second one.
- **Failures:**
  - a missing item code is Failed with the message
  - a 5xx gives backoff
  - 10 attempts give Failed
  - Retry, Retry failed and Skip (audited)
  - a totals mismatch is Failed without cancelling
- **DailySummary mode:**
  - aggregates per item
  - one per business date, and never twice
  - late entries carried to the next day
  - switching mode only affects unsent dates
- **Menu pull:**
  - the preview lists every kind of change
  - Apply creates and updates items, and marks missing ones inactive
  - modifiers and recipes are kept
  - price changes are logged as "ERPNext sync"
  - auto-apply on and off
- **Service never waits:** settle stays under 500 ms with the fake's 30-second delay, and with ERPNext down.
- **The module off:** no rows are written and the worker doesn't run.
- **The rate limit** is respected.

**Web:**
- the switch
- the connection form, with the secret shown as `••••`
- Test connection results
- the mappings dropdowns filled from ERPNext
- Items needing a code, with auto-match
- the tiles updating live
- Recent syncs with links, Retry and Skip
- the pull preview and Apply
- the failed-sync banner

## Part H — Follow-ups to record (don't build)

- Stock movements, deliveries and stock reconciliation to ERPNext.
- Expenses and journal entries to ERPNext.
- Customer accounts and credit sales.
- eTIMS through ERPNext's Kenya compliance app.
- Two-way stock levels.

## Acceptance checklist

- [ ] With the module off, settling works exactly as before, and no ErpSync rows are written.
- [ ] Against a **real ERPNext test site** (I'll provide the URL and an API user), **Test connection** passes every check, after I add the `tausi_receipt_no` custom field from `docs/ERPNEXT.md`.
- [ ] **Pull menu and prices** shows a preview, and Apply brings in items and Standard Selling prices. Tausi's Cold/Warm modifiers and recipes are untouched.
- [ ] Settle a ticket with M-Pesa and cash, a discount and a gift. Within a few seconds a **submitted Sales Invoice** appears in ERPNext, with the same grand total, the payments, VAT included, and `tausi_receipt_no` set. The wireframe's list shows it as Sent, with a link to the invoice.
- [ ] Settle a ticket with an item that has no item code. It shows Failed with the wireframe's message. Add the code, press **Retry failed**, and it's Sent.
- [ ] Stop ERPNext (or point the module at a wrong address) and settle 5 tickets. Settling is just as fast, the tickets queue as Waiting, and they're all sent once ERPNext is back, with **no duplicates**.
- [ ] Switch to **One summary invoice per day**, close the day, and one summary invoice is created with the aggregated items.
- [ ] `docs/ERPNEXT.md` explains the ERPNext side: the API user and its roles, the custom field, the modes of payment, the tax templates, the POS profile (if using POS Invoice), and what Tausi sends.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/16-erpnext-sync` and stop.

When done, report as `AGENTS.md` describes, with what I need to set up in ERPNext, and anything you decided differently from this prompt.
