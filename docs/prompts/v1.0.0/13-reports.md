# Task 13 — Reports

Read `AGENTS.md` first. Use the wireframe **"19 · Admin: reports"** and the **Reports** section of `docs/design.md`. Work on a new branch `screen/13-reports` from `main` (tag `screen-12-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This task builds the admin **Reports** page: one framework, then every report in the design, plus three that a bar needs in practice:
- **Sales by item**
- **VAT summary**
- **Tickets and receipts**, with a drill-down into any ticket

Every report must **agree with the ledger and with the other reports**, print cleanly, and export to Excel. Names are never cut off.

---

## Part A — The report framework

**Server:** add a `Reports` module in Application. Each report is one class with:
- **key and title**
- **the filters it supports**
- **the permission it needs** (`reports.all`, unless stated otherwise)
- **`Run(filters)`**, which returns a typed result:
  - columns (key, label, type, alignment)
  - **groups**, each with a header row and a subtotal row
  - rows
  - **grand totals**
  - notes, such as "Includes 2 open tickets"

Run it with **read-only queries**, using `AsNoTracking` and projections. Never load whole entities, and never use N+1 queries.

**Filters**, shared by every report:
- **Period:**
  - a **work period** (default: the current one, or the last closed)
  - **or** a **range of business dates** (Task 12), with quick picks: Today, Yesterday, This week, Last week, This month, Last month
- **User**, **Terminal**, **Location**, **Category**, **Payment type**, **Supplier**, depending on the report.
- **Credit sales to:** **Who added the item** (the default) or **Who opened the ticket**, for the sales reports.
- **Include open tickets:** on by default for the current work period, and off and hidden for closed ones. When it's on, a note says how many tickets are still open.

**Endpoints:**
- `GET /api/admin/reports`: the list the user may run
- `POST /api/admin/reports/{key}/run`: filters in, result out
- `POST /api/admin/reports/{key}/export?format=xlsx|csv`
- `POST /api/admin/reports/{key}/print?target=pdf|printer&printerId=`

**Sales definition (one shared function).** Use **one** query helper for "sales" everywhere: reports, the daily summary (Task 12), My sales (Task 9) and the Z report.
- Line amount = gross after the line's **discount share**.
- **Voided and cancelled** lines are left out.
- **Gifts** count as 0, and are listed separately at their original value.
- Rounding is a separate figure.

If an earlier task computes sales its own way, switch it to this helper, and note it in DECISIONS.md.

**Output:**
- **Screen:** a table with the wireframe's style: **group header rows** with a grey background and the group total on the right; long names **wrap**; numbers in monospace, right-aligned; a sticky header.
  - Groups with many rows show the first 50, then "… 12 more items", which expands.
  - **Every ticket number is a link** to the ticket detail (Part C).
- **Print (A4 PDF):**
  - Use the small PDF writer from Task 11. Add tables that run over pages, repeated column headers, group headers, subtotals, page numbers, and a header with the business name, the report title, the filters and when it was run.
  - **Wrap text, never cut it.**
  - Portrait, or landscape when there are more than six columns.
- **Print (receipt printer):** for the short reports marked 🧾 below, a 42/32-column version on a chosen printer.
- **Excel:**
  - A real `.xlsx` with one sheet, the filters in the top rows, then the table.
  - Group headers in bold, subtotal rows in bold, numbers stored as numbers with KSh formatting, and dates as dates.
  - Frozen header row, and column widths that fit the content.
  - Use **ClosedXML** (MIT licence). Add it to THIRD-PARTY-NOTICES.
- **CSV:** a flat table with no group rows, with the group key repeated in a column.

**Speed:**
- Any report over **one month** of a busy bar (seed a test set of about 60,000 lines, 4,000 tickets and 6,000 payments) returns in **under 2 seconds** on PostgreSQL and SQL Server in CI.
- Add any indexes needed on business date, work period, user, terminal and status. Add a performance test that fails above 2 seconds.

## Part B — The reports

The **wireframe's tab row** lists the reports. Use this order:

1. **Sales by user** (wireframe)
    - Grouped by user. The header row shows the name, the role and the total.
    - Rows: ticket, time, item (with modifiers), quantity, amount.
    - Group footer: the number of tickets, gifts (original value), voids (count and value), and discounts given on tickets credited to this user.
    - Filters: period, user, terminal, credit sales to, include open tickets. 🧾
2. **Sales by item**
    - Grouped by category.
    - Each item shows the quantity, gross sales, discount, net of VAT, VAT, **cost of sales**, **gross profit** and **margin %**.
    - It also lists gift and void quantities.
    - Cost comes from the stock movements of Task 11. Items with no recipe show "No recipe" in the cost column.
    - Filters: period, category, terminal, user.
3. **Payments and cash-ups**
    - **Per cashier shift:** opening float, cash, M-Pesa and card taken, pay-outs, drops, expected, counted, difference, signed off by.
    - **Per waiter:** cash, M-Pesa and card **brought in**, and their **open bills**, with ticket links.
    - **M-Pesa codes:** each code with its amount, ticket, cashier and time, plus payments with "No code". This is for checking against the till statement.
    - Filters: period, cashier, waiter, payment type. 🧾
4. **Voids, gifts and approvals**
    - Each event: time, kind (Void / Gift / Discount / Price change / Reprint / Move / Merge), ticket, item, quantity, amount, reason and note, **who asked**, **who approved**, terminal, and for voids whether it was poured.
    - Also rejected and expired requests.
    - Totals by kind, and **by approver**.
    - Filters: period, kind, requester, approver.
5. **Price changes**
    - **Menu price changes** from PriceChangeLog: item, price list, old, new, who, when.
    - **On-ticket price changes**: ticket, item, old, new, quantity, the difference in KSh, who, approved by, when.
    - Filters: period, kind, user.
6. **Stock on hand**
    - Each stock item per location: quantity (in packs and base units), average cost, value, reorder level, and whether it's below the reorder level or below zero.
    - Grouped by category, with value subtotals.
    - Filters: **as at** a date (worked out from movements, not the cached figure), location, category, "only below reorder".
    - It doesn't use a period.
7. **Stock variance**
    - One approved count, chosen from a list (default: the last one).
    - Expected, counted, the difference in units and **KSh**, who counted, who approved, and the as-at time.
    - Totals: items short, items over, and the net variance.
    - It also has a **trend** view: variance in KSh per count for one item, over the last 10 counts.
8. **Purchases**
    - Deliveries grouped by supplier: date, invoice, location, total, payment type, reversed.
    - The supplier footer shows **owed at the start**, + deliveries on credit, − payments (Task 12), = **owed at the end**.
    - Filters: period (business dates), supplier, location.
9. **Daily summary**
    - The Task 12 daily summary, for a **range** of business dates.
    - One row per date with sales (incl. VAT), VAT, net, cost of sales, gross profit, expenses, stock loss and cash differences, net, then the range totals.
    - Choosing a single date shows the same vertical layout as the cash book. 🧾
10. **VAT summary**
    - Per VAT rate: gross sales, net and VAT, after discounts, with gifts at 0.
    - Exempt and zero-rated are shown on **separate** lines.
    - **Per day**, with totals.
    - It must equal the VAT payable credits in the ledger for the same dates. It's ready for the owner's accountant now, and for eTIMS later.
11. **Tickets and receipts**
    - Every ticket in the period: ticket and receipt numbers, table, opened by, opened and settled times, total, discount, payment types, status (Settled / Open / Cancelled / Merged), and the number of prints and reprints.
    - Filters: status, user, table, an amount range, and "has voids" / "has reprints" / "has discount".
    - It's the starting point for checking anything suspicious.
12. **Users, roles and terminals**
    - Each user: their role, the terminals they logged in on, their first and last login, tickets opened, items added, voids requested, and approvals given.
    - Filters: period, role.
13. **Audit log**
    - The raw audit entries: time, user, terminal, action, entity, and before/after (collapsible).
    - Filters: period, user, action, entity, and a text search.
    - It needs `permissions.manage`, because it can show sensitive changes.

Put the long tab row on **two lines**, or use a **"More reports"** menu after the first seven, so it never needs scrolling sideways.

## Part C — Ticket detail (drill-down)

`/admin/tickets/{id}` is **read-only**. Any ticket link in any report opens it. It shows:
- **Header:** ticket and receipt numbers, table, status, opened by, at and on (terminal), settled by and at, and the work period and business date.
- **Lines:** every line, **including cancelled, voided and gifted** ones, each with its status, who added it, the terminal, the time, the print round, price changes and the discount share.
- **Prints:** every bill, dispense slip, VOID slip, receipt and reprint, with its number, printer, time, who, and the job's status. **View slip** shows the exact text that was printed.
- **Payments:** type, amount, code, taken by, brought by, shift, status (including removed ones).
- **Approvals:** every request on this ticket and how it ended.
- **Stock and ledger:** the movements and journal entries it caused, with links to the Accounts ledger.
- **Timeline:** all of the above merged, in time order. This is the view for answering "what happened on Table 7 last night?".
- **Print** for the whole page as a PDF.

To show the exact slip text, **store the rendered text of each print job**, if it isn't stored already. Keep it for `print.keepSlipTextDays`, a new setting with a default of 365, and clear it with a nightly job.

## Part D — Terminal side

- **My sales** (Task 9) uses the shared sales helper, and still shows only the user's own figures.
- **Supervisors' slip:** a **Sales by user** 🧾 slip can be printed from the **Close day** screen for users with `reports.all`, for the period being closed.

## Part E — Tests

**Agreement tests,** over the seeded test data, including discounts, gifts, voids (partial, poured and not poured), price changes, rounding, a merged ticket, an open ticket and a reversed delivery:
- Sales by user (both crediting choices) = Sales by item = Daily summary sales = Tickets and receipts totals = the ledger's Sales + VAT for the same period.
- The VAT summary equals the VAT payable in the ledger, per rate and per day.
- Payments and cash-ups equals the Cash, M-Pesa and Card clearing debits from settles.
- Stock on hand as at a date equals the sum of movements up to that date, and today's figure equals the cached on hand.
- Stock variance equals the count's journal entry.
- The Purchases owed at the end equals Supplier payables in the ledger.
- My sales for each user equals their group in Sales by user (credited to who added the item).

**Other server tests:**
- **Permissions:** `reports.all` for most reports and `permissions.manage` for the audit log. A user with only `reports.own` gets nothing from the admin endpoints.
- **Filters:** each filter on each report, the business-date ranges past midnight, and the include-open-tickets note.
- **Exports:** the xlsx opens, its numbers are stored as numbers, and its group and subtotal rows are bold. CSV columns are flat.
- **Printing:** the PDF wraps long names (a 120-character test item), repeats headers over pages, and has page numbers. The 🧾 receipt versions are checked as golden slips at 42 and 32 columns.
- **Speed:** the performance tests stay under 2 seconds, on both providers.
- **Ticket detail:** it includes every line status, print, payment, approval, movement and journal entry, and the stored slip text matches what was sent to the printer.

**Web:**
- the tab row, or the More menu
- filters per report
- group rows with "… n more" expanding
- wrapping names
- ticket links opening the detail
- the Print and Export buttons
- the empty state
- the ticket detail timeline

## Acceptance checklist

- [ ] On the demo data, **Sales by user** looks like the wireframe, with grouped users, their roles and totals, and wrapped names. Switching to "Who opened the ticket" changes the credits, and the grand total stays the same.
- [ ] Sales by user, Sales by item, Daily summary, VAT summary, and Tickets and receipts all show the **same sales total** for the same period, and it matches the ledger.
- [ ] Sales by item shows cost, gross profit and margin per item, and "No recipe" where there's none.
- [ ] Payments and cash-ups lists each M-Pesa code with its ticket. CAROL's shift shows its difference and who signed it off.
- [ ] Voids, gifts and approvals shows who asked, who approved, and poured or not, with totals per approver.
- [ ] Export Sales by user to Excel. It opens with bold group rows and numbers you can add up.
- [ ] Print it to A4. A 120-character item name wraps and isn't cut, and the pages are numbered.
- [ ] Click a ticket number. The ticket detail shows its full timeline, including a voided line, its VOID slip text and the corrected bill.
- [ ] A month of the test data loads each report in under 2 seconds.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/13-reports` and stop.

When done, report as `AGENTS.md` describes, with anything you decided differently from this prompt.
