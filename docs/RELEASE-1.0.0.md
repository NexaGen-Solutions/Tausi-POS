# Tausi POS 1.0.0 — release notes (draft)

Tausi POS by NexaGen Solutions. Simply Outstanding.

> Draft for the owner. The owner tags `v1.0.0`, which builds the installers and makes a draft GitHub
> release; these notes go with it.

## What's in 1.0.0

Tausi POS runs a bar or restaurant on its own server, with touch terminals on the counters and an admin
site in a browser. No internet is needed during service. The full list is the 1.0.0 entry in
`CHANGELOG.md`; in short:

- PIN login, the live table map, ordering with Cold/Warm, and one Print button (the bill first, then
  only the new items for the counter).
- Voids, gifts, discounts and price changes under a permissions grid, with supervisor approval by PIN or
  from their own counter.
- Cashier-only settling: cash with who brought it, M-Pesa codes, card and split payments.
- Shifts, cash-ups, My sales and closing the day with a Z report.
- Training mode for chosen terminals, kept out of the real figures.
- Menu, prices, stock (deliveries, transfers, waste, counts), the cash book and a daily profit summary on
  a double-entry ledger.
- Reports that print and export to Excel.
- A setup wizard, installers for Windows and Linux, nightly checked backups with a second copy, updates
  after closing time, and Download diagnostics for support.

## System requirements

| | Server PC | Each counter PC |
| --- | --- | --- |
| Operating system | Windows 10 or 11 (64-bit) or Windows Server; or Ubuntu 22.04 / 24.04 | Windows 10 or 11 (64-bit) |
| Database | SQL Server Express (installed for you) or an existing SQL Server; PostgreSQL 16 on Linux | — |
| Memory | 4 GB or more | 4 GB or more |
| Disk | An SSD with at least 20 GB free | Any |
| Network | Wired, with a fixed IP address | Wired if possible |
| Power | A UPS (battery backup) | |
| Printers | ESC/POS thermal printers: on the network (port 9100), shared from a Windows PC, or USB on a terminal | |

Windows 7 and 8 are not supported on the counters. Performance figures are in `docs/PERFORMANCE.md`.

## Known limits

These are not in 1.0.0 (see "After v1" in `docs/DECISIONS.md`):

- **ERPNext sync**: moved to a later version by the owner. Menu items already keep an ERPNext item code.
- **KRA eTIMS**: VAT is stored on every line so it can be added.
- **M-Pesa STK push**, card-terminal integration, phone ordering, kitchen display screens.
- **Refunds and un-settling** a ticket settled by mistake.
- **Approvals on the admin site** (they happen on the terminals) and an **opt-in remote support mode**:
  waiting for the owner's decision (`docs/V1-CHECKLIST.md`).
- **Payroll, a balance sheet, multiple warehouses** (the store and the bar are supported).
- Printing to Windows-shared and USB printers is tested in CI on Windows, but has to be confirmed on each
  site's printers during the pilot.

## Before selling

- Replace the development **licence public key** with the owner's real key (DECISIONS.md: Setup wizard
  and licences). Keys made with the development key stop working then; pilot sites get a new key.
- Sign the installers with the code-signing certificate (`scripts/sign.ps1`), so Windows doesn't warn.

## Upgrading from pilot builds

Pilot builds are versions 0.x. Data carries over; nothing is entered again.

1. **Close the day** on a terminal, so nobody is mid-ticket.
2. **Server (Windows):** run `TausiPOS-Server-Setup-1.0.0.exe` on the server PC. It keeps the settings,
   the database and the backups, takes a backup first, updates the database, and goes back to the
   previous version by itself if anything fails. Or, with the day closed, use Settings → System →
   *Update from a USB drive* with the `.tausiupdate` file.
3. **Server (Linux):** unpack `tausi-server-1.0.0-linux-x64.tar.gz` and run `sudo ./install.sh` again;
   it backs up first and goes back if it fails.
4. **Terminals** update themselves from the server the next time they start, before anyone logs in.
   A counter PC that was set up by hand can also run `TausiPOS-Setup-1.0.0.exe`.
5. Open **Settings → Backups → Back up now** and check it ends with **Verified**. Pilot builds before
   `release-15-v3` couldn't verify SQL Server backups; 1.0.0 grants the permission this needs.
6. Demo data used for training in a pilot can be cleared with **Settings → Training mode → Clear
   training data**. Real data from the pilot stays.
