# Tausi POS pilot plan

A template for NexaGen Solutions and each new customer: running Tausi POS **next to the customer's old
POS** (for example SambaPOS) before switching over. Copy it for each site and fill in the blanks.

| | |
| --- | --- |
| Business | `[BUSINESS NAME]` |
| Address | `[ADDRESS]` |
| Owner / manager | `[OWNER NAME]`, `[PHONE]` |
| NexaGen engineer | `[ENGINEER NAME]`, `[PHONE]` |
| Server PC | `[SERVER PC]` (`[SERVER IP]`, fixed) |
| Counters | `[COUNTER A]`, `[COUNTER B]`, `[PILLAR 1]`, `[PILLAR 2]` |
| Old POS | `[OLD POS]` (for example SambaPOS `[VERSION]`) |
| Start date (week 0) | `[DATE]` |
| Agreed stock variance limit | `[LIMIT]` (for example 1% of stock value) |

The plan takes about four weeks. A week only starts when the one before it is signed off.

---

## Week 0 — Setup

1. **Install the server** on `[SERVER PC]` (docs/INSTALL.md):
   - If it already runs SQL Server Express for the old POS, choose **Use an existing SQL Server** and keep
     the database name **`TausiPOS`**. It's a separate database: nothing in the old POS's database is
     touched.
   - Otherwise let the installer install SQL Server Express.
2. **Run the setup wizard** at `http://[SERVER IP]:5080/setup`: business details, the first admin and
   PIN, then **import the menu, tables and staff from SambaPOS** (step 4: *From SambaPOS*). Check the
   import's counts against the old POS before going on.
3. **Fixed IP and power**: give `[SERVER PC]` a fixed IP (a DHCP reservation in the router), set it never
   to sleep, keep its network card awake, and put it on a UPS.
4. **Backups**: Settings → Backups → set the **second copy** (a USB drive or a network folder), then
   **Back up now** and check it ends with **Verified**. Download one backup and keep it off site.
5. **Printers**: add each printer under Terminals and printers, route each counter, and press **Test print**
   on every one.
6. **Install the terminal app on `[COUNTER A]` only**, alongside the old POS, and pair it. The old POS
   stays the till for now.
7. Fill in **Licence** (Settings → Licence): a trial or the customer's key.

**Sign-off:** server installed, backups verified with a second copy, every printer printed a test,
Counter A paired. `[ENGINEER]` ____ `[OWNER]` ____ Date ____

## Week 1 — Training

1. Switch **Counter A to training**: Settings → **Training mode**. Its screens show an orange band and
   every slip says TRAINING. Nothing done on it counts.
2. Each staff member practises, with the NexaGen engineer or the manager watching, using the printed
   guides in `docs/guides/` (waiter, cashier, supervisor, owner). Tick each line on the **training
   checklist** below.
3. At the end of the week: **Settings → Training mode → Clear training data**, and switch training off.

### Training checklist (one per person)

Name: ____________ Role: ____________

| Task | Done | Checked by |
| --- | --- | --- |
| Log in with their own PIN, and log out | ☐ | |
| Open a table and add items, including a drink with Cold / Warm | ☐ | |
| Print, and read the bill and the dispense slip | ☐ | |
| Add a second round and print again (only the new items go to the counter) | ☐ | |
| Void a printed item: by a supervisor's PIN, and by a request to the supervisor's counter | ☐ | |
| My sales: their own figures and any open bills | ☐ | |
| **Cashiers:** start a shift with the float | ☐ | |
| **Cashiers:** settle with cash and change, recording who brought it | ☐ | |
| **Cashiers:** settle with M-Pesa, typing the code | ☐ | |
| **Cashiers:** a split payment (part cash, part M-Pesa) | ☐ | |
| **Cashiers:** a cash-up: blind count, supervisor sign-off, cash-up slip | ☐ | |
| **Supervisors:** approve a request from their own counter; close the day | ☐ | |

Signed: staff ____ trainer ____ Date ____

## Week 2 — Parallel run on one counter

1. **Counter A uses Tausi POS for real** for one shift a day (`[SHIFT, e.g. 18:00 to closing]`). The other
   counters stay on the old POS. Check training mode is **off** on Counter A.
2. **Each night, compare** Tausi POS with the till and the stock, on the comparison sheet below. Take the
   figures from Tausi POS's reports (*Daily summary*, *Payments and cash-ups*) and from the M-Pesa till
   statement.
3. **Log every problem** in `docs/PILOT-LOG.md` (a copy per site): date, what happened, who saw it, and the
   severity (below). NexaGen fixes them as patch releases (1.0.x), installed after closing time.

### Nightly comparison sheet

| Date | Counter A sales (Tausi POS) | Cash counted vs expected | M-Pesa total (Tausi POS) | M-Pesa total (till statement) | M-Pesa codes all matched? | Counted items: stock difference | Notes | Initials |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| | | | | | ☐ | | | |
| | | | | | ☐ | | | |
| | | | | | ☐ | | | |
| | | | | | ☐ | | | |
| | | | | | ☐ | | | |
| | | | | | ☐ | | | |
| | | | | | ☐ | | | |

The M-Pesa codes come from the *Payments and cash-ups* report (export to Excel), one per payment; each
must appear once on the till statement.

**Sign-off:** seven nights compared, no open Severity 1 problem. `[ENGINEER]` ____ `[OWNER]` ____

## Week 3 — All counters

1. Install and pair the terminal app on `[COUNTER B]`, `[PILLAR 1]` and `[PILLAR 2]`. All four counters
   use Tausi POS.
2. **The old POS stays installed but unused, as a fallback.** Nobody uses it unless the procedure below
   says so.
3. Do the **first full stock count** (Stock counts), and the **first full week of cash-ups**.
4. Keep comparing each night and logging problems.

### If Tausi POS is down

Service never waits for a fix. The manager on duty decides, in this order:

1. **One counter down** (its PC or the terminal app): serve that counter's tables from the next counter.
   Restart the PC; the app comes back to the PIN screen by itself.
2. **A printer down**: the screen shows "Not printed · Retry". Move the printer's jobs to another printer
   (Terminals and printers → the printer → its failed jobs → Send to another printer), and call NexaGen.
3. **The server down** (every terminal shows "Not connected" for more than 5 minutes):
   1. Call NexaGen: `[NEXAGEN SUPPORT PHONE]`.
   2. Restart the server PC. Wait 3 minutes.
   3. Still down: **switch to the old POS** on every counter. Write the open tickets' table numbers and
      items on paper from the last printed bills, and enter them in the old POS.
   4. Note the time you switched, and the time Tausi POS came back, in `PILOT-LOG.md` (Severity 1).
   5. Don't go back to Tausi POS mid-shift: switch back at the start of the next day, after NexaGen says
      it is fixed.

**Sign-off:** all four counters live, first full stock count done. `[ENGINEER]` ____ `[OWNER]` ____

## Week 4 — Sign-off

The owner signs off when **all** of these hold:

- **7 days in a row** with no Severity 1 problem.
- **Cash-ups balance** every day (differences explained and within the house rule).
- **Stock variance** at the weekly count is within `[LIMIT]`.
- Backups have been **Verified** every night, with the second copy working.

Then **remove the fallback**: uninstall the old POS from the counters (keep its database backup), and
switch the support line to normal hours.

Owner sign-off: ____________ Date ____ NexaGen: ____________ Date ____

---

## Severity

| Severity | Means | Examples |
| --- | --- | --- |
| **Sev 1** | Service stops, or money or stock is wrong | The server is down; a payment or line is lost or doubled; totals don't match the till; stock moves wrongly |
| **Sev 2** | A real problem, but a workaround exists | A printer needs its jobs moved by hand; a report shows a wrong column; one screen is slow |
| **Sev 3** | Cosmetic | A label is cut off; a colour is wrong; a typo |

## NexaGen's response times (during the pilot)

| Severity | First answer | On site or remote fix |
| --- | --- | --- |
| Sev 1 | 15 minutes, any time the bar is open | Within 2 hours; a patch release within 24 hours |
| Sev 2 | Within 4 working hours | In the next patch release (within 1 week) |
| Sev 3 | Next working day | In a later release |

## Daily check (manager, each morning)

| Check | Where | OK? |
| --- | --- | --- |
| Last night's backup ended **Verified**, and the second copy worked | Settings → Backups (no red banner) | ☐ |
| No **failed prints** waiting | Terminals and printers (each printer's status) | ☐ |
| Yesterday's day is closed and every cash-up signed off | Reports → Payments and cash-ups | ☐ |
| All terminals **online** | Terminals and printers | ☐ |
| Licence: no warning banner | Any admin page | ☐ |
