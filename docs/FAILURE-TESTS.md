# Failure tests

What Tausi POS does when something goes wrong during service (task 16, Part C). Each scenario has its
automated tests, and a **manual test** to run on real hardware before a pilot (on the pilot site's
server, with the day open on a test counter, or on a spare PC).

After any of these, run the consistency check on the server:

```bat
"C:\Program Files\Tausi POS\Server\TausiServer.exe" check
```

(Linux: `sudo -u tausi env $(cat /etc/tausi/tausi.env | xargs) /opt/tausi/TausiServer check`.) It must end with **Everything agrees**: the books
balance, every settled ticket is paid in full with one sale entry, and stock on hand matches its
movements. The same check is at `GET /api/admin/system/check` (settings.manage).

---

## 1. The server restarts mid-service

**Expected:** terminals show "Not connected to the server. Reconnecting…", reconnect by themselves, and no
line, print or payment is lost or doubled. Print jobs that were waiting still print, in order.

**Automated:**
- `PrintJobProcessorTests.Jobs_waiting_when_the_server_restarts_print_after_it_in_order`: the queue is in
  the database; a new server prints the waiting bill, slip and receipt in order.
- `IdempotencyTests.Repeated_key_returns_the_original_response_without_running_again`: a tap repeated after
  the restart returns the first answer (the keys are in the database for 24 hours).
- `OfflineBanner.test.tsx` ("appears when the server cannot be reached and clears when it is back").

**Manual:**
1. On Counter A, open a table, add 3 items, press Print. Unplug the bill printer first, so its jobs wait.
2. On the server: `sc stop TausiPOSServer` (Linux: `sudo systemctl stop tausi`). Every terminal shows the
   orange "Not connected" banner within 10 seconds.
3. On Counter B, tap a table: nothing happens and nothing is lost.
4. `sc start TausiPOSServer`. Within 30 seconds every banner goes; screens show the same tickets.
5. Plug the printer in: the waiting bill prints before its dispense slip.
6. Run `TausiServer check`: **Everything agrees**. The ticket has 3 lines, not 6.

## 2. The network drops on one terminal while it adds items

**Expected:** taps already queued either all arrive, in order, or each says it wasn't added. None is lost
without the screen saying so.

**Automated:**
- `OrderPage.test.tsx` ("when the network drops mid-order, each tap arrives in order or says it was not
  added") and ("10 fast taps are sent one after the other and make a quantity of 10").
- `OrderScreenApiTests.Two_presses_at_once_print_one_round_and_another_terminal_can_not_print`.

**Manual:**
1. On Counter A, open a table and tap Tusker Lager 5 times quickly, then pull the network cable at once.
2. Each tap that couldn't go shows "Not connected: Tusker Lager was not added…".
3. Plug the cable back. The ticket shows exactly the lines the server has: the quantity matches the
   taps that didn't show the message.
4. Print: the dispense slip has that same quantity.

## 3. A printer is off or out of paper

**Expected:** its jobs fail after 4 tries (2, 4, 8 seconds apart), the order screen shows "Not printed ·
Retry", Retry works, and the bill still comes out before the dispense slip. The ticket itself is saved.

**Automated:**
- `PrintJobProcessorTests.Retries_three_times_with_backoff_then_fails_and_publishes` and
  `PrintJobProcessorTests.Bill_prints_before_dispense_slip_even_after_a_retry`.
- `PrinterAdminTests.Retry_keeps_the_bill_before_its_dispense_slip_even_across_printers`.
- `PrintTicketTests.A_printer_that_fails_never_undoes_the_print_round_or_the_ticket`.
- `OrderPage.test.tsx` ("shows a failed bill with Retry, live and when reopened").

**Manual:**
1. Switch the bill printer off. On Counter A, add 2 items and press Print.
2. Within 15 seconds: "Not printed · Retry" on the ticket; Terminals and printers shows the printer's
   error.
3. Switch the printer on and press Retry: the bill prints first, then the dispense slip.
4. Out of paper: the same, with the cover open.

## 4. The database is unavailable for 30 seconds

**Expected:** the API answers **503** with `code: server_busy` and `Retry-After: 5`; terminals show
"Server busy, retrying…" over the screen; everything comes back by itself when the database answers.

**Automated:**
- `UnavailableTests` (which errors mean "the server is busy", and the 503 with its code and Retry-After).
- `Failures.test.tsx` ("shows Server busy, retrying while the database is away, and clears when it
  answers").

**Manual:**
1. With service running, stop the database for 30 seconds: on Windows `net stop MSSQL$TAUSI` (or
   `MSSQL$SQLEXPRESS`), then `net start` it; on Linux `sudo systemctl stop postgresql`, wait, `start`.
2. Tapping a table on a terminal shows "Server busy, retrying…".
3. When the database is back, the message goes within 3 seconds and the tap can be repeated.
4. Run `TausiServer check`: **Everything agrees**.

## 5. The disk is full

**Expected:** a write fails cleanly with **503** `code: disk_full` ("The server's disk is full, so nothing
can be saved…") and nothing half-written (each write is one transaction). The admin site shows an orange
banner when less than 1 GB is free, before it gets that far. Backups report the failure with the red
banner.

**Automated:**
- `UnavailableTests` (PostgreSQL's disk_full, SQL Server's 1105/9002, ENOSPC and ERROR_DISK_FULL become
  503 disk_full).
- `Failures.test.tsx` ("warns on the admin site when the server has under 1 GB free").
- `BackupApiTests.A_failed_backup_and_a_failed_copy_show_banners_but_a_copy_failure_keeps_the_backup` and
  `System.test.tsx` ("shows a red banner when a backup failed or none worked for 48 hours").

**Manual** (on a test PC, never on a live server):
1. Fill the data drive: `fsutil file createnew C:\fill.bin <bytes>` leaving about 500 MB free.
2. The admin site shows "The server's disk is nearly full".
3. Fill it completely; add an item on a terminal: the message says the disk is full. **Back up now**
   fails and the red backup banner shows.
4. Delete `C:\fill.bin`. Adding works again; **Back up now** ends Verified.
5. Run `TausiServer check`: **Everything agrees**.

## 6. A power cut on the server

**Expected:** after the restart the database is consistent, and every settled ticket has its journal
entry. A **UPS** is required (`docs/INSTALL.md`, "What you need") so this stays rare.

**Automated:**
- `ConsistencyCheckTests` (what `TausiServer check` finds after real service, and that it catches a lost
  sale entry, a short payment, an unbalanced entry and a wrong stock figure).
- `SettleApiTests.M_pesa_and_cash_settle_once_with_a_receipt_a_free_table_and_a_balanced_entry`: settling
  writes the payments, the receipt and the journal entry in one transaction.

**Manual:**
1. During a busy test (several terminals adding, printing and settling), pull the server's power cable
   (or, in a virtual machine, power it off).
2. Start it again. The service starts by itself; terminals reconnect.
3. Run `TausiServer check`: **Everything agrees**. Any ticket being settled at the cut is either settled
   with its entry, or still open; never half.

## 7. The clock is wrong on a terminal

**Expected:** the terminal's clock doesn't matter: the server stamps every ticket, line, payment, print and
day with its own clock.

**Automated:**
- `ServerTimeTests.No_request_carries_a_time_of_day_from_the_client`: no request from a terminal or the
  admin site carries a time (calendar dates the admin picks are dates only).

**Manual:**
1. Set Counter A's Windows clock 3 hours wrong (and its time zone to another one).
2. Open a ticket, add items, print, settle.
3. The bill, the receipt and the Tickets report show the server's time; the ticket belongs to today's day.

## 8. Two cashiers settle the same ticket, or two supervisors approve the same request

**Expected:** it happens once.

**Automated:**
- `SettleApiTests.Two_settles_of_one_ticket_at_once_settle_it_once` and
  `SettleApiTests.Receipt_numbers_never_repeat_when_settled_in_parallel`.
- `VoidApprovalApiTests.A_request_is_listed_for_approvers_and_two_supervisors_approving_at_once_make_one_void`
  and `ApprovalRequestTests.Approving_twice_fails_the_second_time`.
