# Changelog

Tausi POS by NexaGen Solutions. Each release's changes, newest first (built by the release workflow from the commit messages).

## 1.1.0 (unreleased)

New features. Install 1.0.1 first: it changes how terminals update.

### Added

- **Reports on the terminal.** A **Reports** button on the table map opens Sales by user, Daily sales and
  Sales by item for Today, Yesterday, This week or picked dates, with big totals and a print-out on the
  terminal's receipt printer. Waiters see only their own figures; supervisors and admins see everyone and
  can choose a user or terminal. The terminal shows sales only, never cost or profit. The figures are the
  same as the admin site's reports for the same dates.
- **Daily sales** report on the admin site too: sales, discounts, VAT, payments by type, tickets, open
  tickets, voids and gifts.
- **M-Pesa.** Send a PIN prompt to the customer's phone from the settle screen (or, if allowed, from the
  order screen), print a "Scan to pay" QR on every bill, and see payments made to the till on the settle
  screen to add to a bill. A payment can pay parts of several bills and never gives change. Typed codes are
  checked against Safaricom's copy, and flagged after 24 hours if it never comes. The cashier still
  completes every bill. A new **M-Pesa payments** page on the admin site shows what was received, applied
  and not yet applied, with Apply to ticket, Mark as not from POS and an Excel export. Close day warns
  about unapplied payments. Set it up under **Settings → M-Pesa** (see `docs/MPESA.md`). Safaricom's
  messages reach the bar through NexaGen's Tausi relay (`docs/RELAY.md`).

## 1.0.1 (unreleased)

Fixes for 1.0.0. **Each counter PC needs the new `TausiPOS-Setup-1.0.1.exe` run once by hand**, because
this version fixes how terminals update; after that they update from the server as before.

### Fixed

- **Sales now count on the day they happened.** A work period left open for several days put every sale
  on the day it was opened. Now each item counts on the date it was served and each payment on the date
  it was taken, in the business's time zone, with the day changing at midnight. Reports filter by dates
  first (Today by default), with work periods as a second choice. A Z report for a work period that ran
  over several days shows each day's sales and takings, with a warning. Upgrading moves the sales already
  recorded to the right dates; a backup is taken first.
- **A terminal app can no longer vanish after a failed install.** Reinstalling over an older version first
  removed it, so an install that was then blocked (for example by antivirus) left an empty folder. The
  installer now keeps the previous version, and a new launcher starts the app, restarts it if it stops,
  goes back to the previous version if a new one doesn't start within a minute, and offers **Repair**
  (from the server, keeping the pairing) if the app is missing.
- **The Terminals page shows each terminal's last update and why it last stopped**, with a warning when
  an update failed or was rolled back.
- **Security:** the terminal's updater library is updated (its zip handling had published security
  issues). Updates were already safe, because every installer's signature is checked first.

## 1.0.0 (2026-11-03)

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
  counter terminals (Windows 10 and 11), signed by NexaGen Solutions, with a **Tausi POS Admin**
  shortcut on the server PC.
- **Backups every night and again at 18:00**, checked after each run and copied to a second place,
  with restore from the admin site. Updates install after closing time; terminals update themselves.
- **Download diagnostics** for NexaGen support.
- Licence keys with a trial and a grace period, so service never stops.
