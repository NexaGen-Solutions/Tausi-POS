# Changelog

Tausi POS by NexaGen Solutions. Each release's changes, newest first (built by the release workflow from the commit messages).

## 1.0.0 (2026-10-28)

The first release of Tausi POS: everything a bar or restaurant needs to run service, cash, stock and the
books on its own server, with no internet needed.

### Service

- **PIN login** on each counter, with the business's own name and logo. Each person has their own PIN,
  so every order, void and payment is recorded under their name and terminal.
- **Live table map** on every terminal: free, occupied and idle tables, several tickets per table,
  takeaway tickets, moving and merging.
- **Order screen** with categories, search, Cold/Warm and other choices, ticket notes, gifts, discounts
  and price changes.
- **One Print button**: the customer's full bill first, then a slip with only the new items for the
  counter. Printing goes through the server to network, Windows-shared or USB printers, and a failed
  print shows "Not printed · Retry" without losing anything.
- **Voids and approvals**: a supervisor approves with their PIN on the spot, or from their own counter.
- **Settle (cashier only)**: cash with change, M-Pesa with the code, card and split payments, recording
  who brought the money. Receipts list every item and the VAT included.
- **Training mode** for chosen terminals: staff practise on demo-style tables without touching real
  sales, reports, stock or the books.

### Cash and the day

- **Shifts and cash-ups**: opening float, cash drops and pay-outs, a blind count by denomination,
  supervisor sign-off and a cash-up slip.
- **My sales** for each waiter: their tables, what was settled and any bills still open on their name.
- **Close the day** only when every ticket is settled, with the day's Z report.

### Back office (admin site)

- Menu and prices, price lists, choices and recipes; import from a spreadsheet or from SambaPOS.
- Stock: items, suppliers, deliveries, transfers between the store and the bar, waste, and stock counts
  with the variance in units and shillings.
- Cash book, expenses, supplier payments and a daily profit summary, all on a double-entry ledger that
  always balances.
- Staff and PINs, a permissions grid (Allowed, Needs approval, Not allowed), terminals and printers,
  tables and settings.
- Reports: sales by user and by item, payments and cash-ups, voids and approvals, price changes, stock
  on hand and variance, purchases, the daily summary, VAT, tickets and receipts, users and terminals,
  and the audit log. Each prints and exports to Excel.

### Install and support

- A first-run **setup wizard**: business details, the first admin, terminals and printers, the menu,
  opening stock and the licence.
- **Installers** for the server (Windows, with SQL Server Express or PostgreSQL; or Linux) and the
  counter terminals (Windows 10 and 11), with a **Tausi POS Admin** shortcut on the server PC.
- **Nightly backups**, checked after each run and copied to a second place, with restore from the
  admin site. Updates install after closing time; terminals update themselves.
- **Download diagnostics** for NexaGen support.
- Licence keys with a trial and a grace period, so service never stops.
