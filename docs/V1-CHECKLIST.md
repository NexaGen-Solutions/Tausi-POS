# Tausi POS v1 checklist

Every requirement in `docs/design.md` and every wireframe in `docs/wireframes/tausi-pos-wireframes.pdf`,
checked against the code for version 1.0.0 (task 16, 28 Oct 2026).

**Done** · **Done differently** (why, with the DECISIONS.md section) · **Later version** · **Missing**

## For the owner to decide

Larger items found missing or open. None is built; each needs your decision.

| Item | From | What it would take |
| --- | --- | --- |
| **Opt-in support mode** | design.md, Product setup: "an opt-in support mode" | Needs a definition first: remote access for NexaGen (a remote-desktop tool, a VPN, or a support login on the admin site that the owner switches on for a set time). The "Send diagnostics" half is built (Settings → System → Support). A support login with an expiry is about 1–2 days. |
| **Approvals on the admin site** | design.md, Roles and approvals; DECISIONS Follow-ups | Supervisors can approve only on a terminal (PIN pad or their counter's request list). Deciding requests from the admin site is about a day. |
| **Real licence public key** | Product setup and licensing | The owner's signing key replaces the development key before selling; a one-line change plus a key ceremony (DECISIONS: Setup wizard and licences). |

The ERPNext sync is **Later version (owner's decision)**: see "After v1" in DECISIONS.md.

## design.md

### Overview, "In this version"

| Requirement | State | Proof |
| --- | --- | --- |
| First-run setup wizard (business, logo, VAT, first admin) and licence key | Done | `SetupFlowTests`, `SetupApiTests`; wireframe 21 |
| PIN login, live table map, ordering with Cold/Warm | Done | `PinAuthenticatorTests`, `TicketsApiTests`, `OrderScreenApiTests` |
| One Print button: bill first, then new items; the server prints | Done | `PrintTicketTests.Print_queues_the_bill_with_every_line_first_then_the_dispense_slip_with_only_new_lines` |
| Void, gift, reprint, price change by the permission grid | Done | `VoidGiftTests`, `VoidApprovalApiTests` |
| Supervisor approvals on the PIN pad or their own counter | Done | `ApprovalRequestTests`, `VoidApprovalApiTests.A_request_is_listed_for_approvers_and_two_supervisors_approving_at_once_make_one_void` |
| Only the cashier settles: cash (who brought it), M-Pesa code, card, split | Done | `SettleApiTests`, `RuleSwitchTests` |
| Receipts list all items and "Total includes VAT" | Done | `SlipsTests` (golden slips) |
| Cash-ups for cashiers and waiters | Done | `CashupTests`, `CashupApiTests` |
| Stock items, recipes, suppliers, deliveries, counts with variance | Done | `StockDocumentTests`, `StockCountTests`, `StockApiTests` |
| Cash book, expenses, daily profit summary, double-entry ledger | Done | `CashBookTests`, `MoneyApiTests` |
| Reports with export to Excel | Done | `ReportsTests`, `ReportApiTests` |
| Admin site for menu, prices, stock, staff, permissions, terminals, printers, settings | Done | wireframes 10–19 |
| ERPNext integration | **Later version (owner's decision)** | DECISIONS "After v1" |

"Not in this version" (eTIMS, M-Pesa STK, phone ordering, kitchen display, payroll, balance sheet,
multiple warehouses): **Later version**, as the design says. Stock has a Store and a Bar location
(DECISIONS: Stock), which is not multiple warehouses.

### Components and Data

| Requirement | State | Proof / note |
| --- | --- | --- |
| Server as a Windows or systemd service | Done | `installer/windows/test-install.ps1`, CI `linux-package` |
| SQL Server Express or PostgreSQL, one EF model | Done | `SqlServerMigrationTests`, both migration sets |
| Terminal: full screen, starts with Windows, knows its terminal, updates itself, USB printing | Done | `terminal/src-tauri` tests; CI `terminal-windows` |
| Admin site in a browser on the LAN | Done | Works over plain `http://<server>` too (DECISIONS: Brand, request IDs) |
| Tables in "Data" (business, people, devices, menu, service, money, stock, accounts, system) | Done | EF model; `ErpSync` is Later version |
| Every sale, delivery, expense and stock change writes balanced journal lines | Done | `LedgerPostingServiceTests`, `OnlyThePostingServiceWritesTheLedgerTests`, `CashBookTests.Over_a_full_day_debits_equal_credits_and_the_summary_equals_the_ledger` |
| Every order line records who added it and on which terminal | Done | `OrderScreenApiTests.Who_added_a_line_and_on_which_terminal_come_from_the_session_never_from_the_request` |

### How an order flows, and Printing

| Requirement | State | Proof / note |
| --- | --- | --- |
| Table turns blue on every terminal | Done | `tables.changed` over SignalR; `TablesPage.test.tsx` |
| Each later round: full bill "replaces earlier bills" + new-items slip | Done | `PrintTicketTests`, `SlipsTests` |
| Print with nothing new is a reprint (Reprint permission) | Done | `PrintTicketTests.With_nothing_new_print_is_refused_so_a_double_press_never_prints_twice` |
| Void: supervisor PIN or request; VOID slip; corrected bill; void log | Done | `VoidGiftTests`, `VoidApprovalApiTests` |
| Separate Send / Print bill mode setting | Done | `PrintMode.SendAndBill`; `PrintTicketTests` |
| Server prints directly; bill then slip; paper cut between | Done | `PrintJobProcessorTests.Bill_prints_before_dispense_slip_even_after_a_retry` |
| Network 9100, Windows share, via terminal | Done | `TransportTests`; real-hardware check is a follow-up |
| Routing per terminal | Done | `PrinterAdminTests`, wireframe 17 |
| Slips: bill, dispense, VOID, receipt, cash-up | Done | `SlipsTests` (golden files) |
| Failed prints queued, retried, "Not printed · Retry"; printer status on Terminals page | Done | `PrintJobProcessorTests.Retries_three_times_with_backoff_then_fails_and_publishes`, `PrintTicketTests.A_failed_job_can_be_retried_from_the_order_screen` |

### Roles and approvals

| Requirement | State | Proof / note |
| --- | --- | --- |
| Grid of roles × actions, Allowed / Needs approval / Not allowed, checked on the server | Done | `PermissionServiceTests`, `PermissionsApiTests`, `ApprovalTests.Not_allowed_is_a_plain_403` |
| Default grid | **Done differently** | Expenses for cashiers default to Needs approval, not Allowed (DECISIONS: Cash book; Follow-ups "design.md's permission table") |
| Supervisor adds waiters and cashiers only | Done | `StaffTests.A_supervisor_can_add_and_edit_waiters` |
| Approvals, price and permission changes audited with who asked, who approved, when | Done | `PermissionsAdminTests.Each_changed_cell_is_audited_announced_and_used_on_the_next_request`, `VoidApprovalApiTests` |

### Cash flow and cash-up

| Requirement | State | Proof / note |
| --- | --- | --- |
| Settle records type, amount, M-Pesa code, who brought it (defaults to the waiter) | Done | `RuleSwitchTests.With_brought_by_on_the_cashiers_choice_is_kept_and_printed` |
| Expected cash = float + cash settled − expenses − drops; count by denomination; supervisor signs off | Done | `CashupTests`, `CashupApiTests`; wireframe 9 |
| Waiter check: their tables, what was settled, bills still open on their name | Done | `SlipsTests.A_waiter_slip_lists_sales_money_brought_and_open_bills`; wireframe 8 |
| Day can't close with an open ticket; each sale belongs to one day | Done | `DayClosingTests`, `DayTests` |

### Stock and basic accounting

| Requirement | State | Proof / note |
| --- | --- | --- |
| Stock items vs menu items, recipes (incl. tots) | Done | `SellingStockTests`, `MenuTests` |
| Sales deduct stock; voids return it only if not served (setting) | Done | `RuleSwitchTests`, `SellingStockTests` |
| Deliveries: supplier, items, cost; payable or paid | Done | `StockApiTests`, wireframe 12 |
| Waste with a reason | Done | `StockDocumentTests` |
| Counts: expected vs counted, units and KSh, who counted; approval posts stock loss | Done | `StockCountTests`, wireframe 13 |
| Expenses and cash book; daily summary | Done | `CashBookTests`, wireframe 14 |
| Option-level recipes (a double) | Later version | DECISIONS Follow-ups |

### VAT and receipts

| Requirement | State | Proof |
| --- | --- | --- |
| Prices include VAT; rate in settings; per-item rate and exempt | Done | `MenuItem.EffectiveVatRate`; `VatCalculatorTests` |
| Per-line gross, net, VAT, half away from zero | Done | `VatCalculatorTests.Price_includes_VAT_at_16_percent`, `MoneyTests.Rounds_half_away_from_zero` |
| Receipt contents (name, logo, address, ticket, table, items, "Total includes VAT", payments, change, footer) | Done | `SlipsTests` (golden files) |
| Rounding rule setting; difference stored as its own line | Done | `CashRoundingTests.Difference_is_what_gets_stored_as_a_rounding_line` |

### Reports

| Report | State |
| --- | --- |
| Sales by user (credit to adder or opener), Payments and cash-ups, Voids gifts and approvals, Price changes, Stock on hand, Stock variance, Purchases, Daily summary, Users roles and terminals, My sales | Done (`ReportsTests`, `ReportApiTests`, wireframe 19) |
| Filter by date or work period, export to Excel, names never cut off | Done (`ReportsTests.The_pdf_wraps_a_long_name_repeats_headers_and_numbers_its_pages`) |
| Extra: Sales by item, VAT summary, Tickets and receipts, Audit log | **Done differently** (more than asked; DECISIONS: Reports) |

### Optional: ERPNext integration

**Later version (owner's decision).** The ERPNext item code on menu items stays, so the sync can use it.

### Installation, backups and updates

| Requirement | State | Proof / note |
| --- | --- | --- |
| One installer: service + `TausiPOS` database on SQL Server Express or PostgreSQL | Done | CI `installer-windows`, `linux-package` |
| Fixed IP, firewall port, network power saving off | Done | INSTALL.md (power saving added in this task); the installer opens the port |
| Terminal installer; pairing; starts with Windows; full screen; Windows 10/11 | Done | CI `terminal-windows`, `PairingTests` |
| Nightly backup; second copy off the server | Done | `BackupFileTests`, `BackupApiTests`; the installer test now backs up and verifies as the service's own account |
| Server updated after closing; terminal checks for updates on start and updates itself | Done | `UpdateTests` |
| Running alongside SambaPOS with separate databases | Done | INSTALL.md; `docs/PILOT.md` |

### Product setup and licensing

| Requirement | State | Proof / note |
| --- | --- | --- |
| One install per business | Done | |
| Wizard: business, admin and PIN, terminals and printers with a test print, menu import (CSV or SambaPOS), opening stock, licence | Done | `SetupFlowTests`, `ImportTests` |
| Licence tied to the business and a terminal count; grace period on expiry | Done | `LicenceTests.Expiry_warns_then_gives_grace_then_only_the_listed_features_wait_for_a_key` |
| "Send diagnostics" bundling the logs | Done (this task) | Settings → System → Support; `DiagnosticsZipTests`, `SystemApiTests.Download_diagnostics_gives_a_zip_for_support_without_secrets_and_is_audited` |
| Opt-in support mode | **Missing (owner to decide)** | See the top of this file |

### Development environment

Develop on Ubuntu, build on GitHub Actions, real hardware early: **Done** (`docs/RUNNING.md`, CI). The
build order's last step, ERPNext sync, is **Later version**; task 16 is the release instead.

## Wireframes

| # | Screen | State | Note |
| --- | --- | --- | --- |
| 1 | PIN login | Done | Tausi mark on the left, customer name large (task B1) |
| 2 | Table map | Done | |
| 3 | Order screen (waiter) | Done | |
| 4 | Cold / Warm pop-up | Done | |
| 5 | Void request | Done | |
| 6 | Supervisor approvals | Done | |
| 7 | Settle (cashier only) | Done | |
| 8 | My sales (waiter) | Done | |
| 9 | Cashier cash-up | Done | Blind count (DECISIONS: My sales, shifts and cash-up) |
| 10 | Admin: menu and prices | Done | |
| 11 | Admin: stock and recipes | Done | |
| 12 | Admin: receive a delivery | Done | |
| 13 | Admin: stock count and variance | Done | |
| 14 | Admin: cash book and daily summary | Done | |
| 15 | Admin: staff and PINs | Done | |
| 16 | Admin: permissions | Done | |
| 17 | Admin: terminals and printers | Done | Plus "Pair a terminal" and "Move terminals to a new server" (task 15) |
| 18 | Admin: settings | Done | Settings → Licence, System, Backups as sub-pages (task 15) |
| 19 | Admin: reports | Done | |
| 20 | Admin: ERPNext sync | **Later version (owner's decision)** | The menu entry shows "Coming in a later version" |
| 21 | First-run setup wizard | **Done differently** | The current step's number is gold with deep teal text, as task B1 asked (the wireframe shows teal) |

The admin menu also has **Tables** (table layout, DECISIONS: Tables and tickets), which no wireframe shows.

## AGENTS.md business rules

| # | Rule | Test that proves it |
| --- | --- | --- |
| 1 | The server decides (permissions in the Application layer) | `ApprovalTests.Not_allowed_is_a_plain_403`, `SettleApiTests.A_waiter_cant_settle` |
| 2 | Permissions are data; NeedsApproval needs an approver's token | `PermissionServiceTests.Needs_approval_without_a_token_throws_approval_required`, `PermissionServiceTests.Valid_approval_succeeds_once_and_names_the_approver`, `PermissionsAdminTests.Each_changed_cell_is_audited_announced_and_used_on_the_next_request` |
| 3 | Each line records who added it and on which terminal, from the session | `OrderScreenApiTests.Who_added_a_line_and_on_which_terminal_come_from_the_session_never_from_the_request` (added in this task), `MergeMoveTests.Merging_keeps_the_oldest_ticket_and_every_line_keeps_its_waiter_and_terminal` |
| 4 | Everything important is audited | `PinAuthenticatorTests.Wrong_pin_is_rejected_and_audited_with_attempts_left`, `SettingsAdminTests.Only_the_changed_keys_are_saved_each_audited_with_old_and_new_and_announced`, `PermissionsAdminTests.Each_changed_cell_is_audited_announced_and_used_on_the_next_request` |
| 5 | Money: decimal, VAT per line half away from zero, rounding line | `VatCalculatorTests.Price_includes_VAT_at_16_percent`, `VatCalculatorTests.Gross_is_always_net_plus_vat`, `MoneyTests.Rounds_half_away_from_zero`, `CashRoundingTests.Difference_is_what_gets_stored_as_a_rounding_line` |
| 6 | Ledger: balanced, only through ILedgerPostingService | `LedgerPostingServiceTests.Rejects_an_unbalanced_entry_and_writes_nothing`, `OnlyThePostingServiceWritesTheLedgerTests` (added in this task), `CashBookTests.Over_a_full_day_debits_equal_credits_and_the_summary_equals_the_ledger` |
| 7 | Idempotency: repeats return the original result | `IdempotencyTests.Repeated_key_returns_the_original_response_without_running_again`, `IdempotencyMiddlewareTests.Repeat_returns_the_stored_response_and_does_not_run_again` |
| 8 | Concurrency: ticket locks and row versions | `SettleApiTests.Two_settles_of_one_ticket_at_once_settle_it_once`, `OrderScreenApiTests.Two_presses_at_once_print_one_round_and_another_terminal_can_not_print`, `MenuTests.A_stale_price_edit_gets_a_conflict`, `SettingsAdminTests.A_save_from_an_older_version_is_refused` |
| 9 | Printing through the queue; failures never roll back | `PrintTicketTests.A_printer_that_fails_never_undoes_the_print_round_or_the_ticket` (added in this task), `PrintJobProcessorTests.A_failed_job_does_not_block_the_next_one_for_good` |
| 10 | Print order: bill first, then new items | `PrintTicketTests.Print_queues_the_bill_with_every_line_first_then_the_dispense_slip_with_only_new_lines`, `PrintJobProcessorTests.Bill_prints_before_dispense_slip_even_after_a_retry`, `PrinterAdminTests.Retry_keeps_the_bill_before_its_dispense_slip_even_across_printers` |
| 11 | Only the cashier settles; TakenBy and BroughtBy | `DefaultPermissionsTests.Only_the_cashier_and_above_settle`, `SettleApiTests.A_waiter_cant_settle`, `SlipsTests.A_receipt_with_m_pesa_and_cash_lists_both_and_who_brought_them` |
| 12 | Never block service on optional things | `LicenceTests.Expiry_warns_then_gives_grace_then_only_the_listed_features_wait_for_a_key`, `UpdateTests.Check_says_what_is_new_and_copes_without_internet` |

## Development-only paths

Each is refused on an installed server (anything but Development), with a test:

| Path | Test |
| --- | --- |
| `seed --demo`, `seed --demo-menu`, `require-pin-change`, `close-day`, `reset` | `DevelopmentOnlyCommandsTests.Are_refused_outside_development` |
| The File printer | `PrinterAdminTests.The_File_printer_is_refused_on_an_installed_server_and_allowed_in_development` |
| The tests' licence key | `LicenceTests` (a production `LicenceService` refuses it) |
| Dev endpoints | None exist: every `/api` route is part of the product |
| `Tausi:Test:FailMigration` | **Done differently:** the installer's rollback test needs it in Production. It is off unless written into the settings file by hand, and only the test build's settings file has it. |

`seed` without `--demo` (roles, permissions, ledger accounts) stays: it changes nothing an install hasn't already got.
