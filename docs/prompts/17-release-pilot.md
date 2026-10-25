# Task 17 — Release hardening, v1.0 and the pilot at Mau's Village

Read `AGENTS.md` first. Work on a new branch `release/17-v1-hardening` from `main` (the latest tag).

Do not merge into `main` and do not create tags. Push your branch and stop.

Every feature for v1 is built. This task makes v1 **safe to run a real bar on**:
- load and failure testing on real-life numbers
- the last missing pieces found by a full review
- a training mode for staff
- the pilot plan for running Tausi POS **next to SambaPOS** at Mau's Village before switching over

**No new features** beyond what's listed here. Anything else goes into `docs/DECISIONS.md` under "After v1".

---

## Part A — Full review against the design

1. **Go through `docs/design.md` section by section,** and every wireframe in `docs/wireframes/`. For each requirement, mark it **Done**, **Done differently** (with the reason, linking DECISIONS.md) or **Missing**, in a new file, `docs/V1-CHECKLIST.md`.
2. **Fix every Missing item** that's small (under about half a day of work). List the bigger ones at the top of the checklist for the owner to decide on. **Don't build them.**
3. **Check `AGENTS.md`'s 12 business rules** one by one, and **name the test that proves each one** in the checklist. Add a test where one is missing.
4. **Remove every temporary or dev-only path** that could reach production: dev endpoints, `close-day`, seeding commands, the File printer, the test licence key. Each one must be refused outside Development, with a test per path.

## Part B — Load and endurance testing

Build a **load test** (a .NET console tool, `tools/Tausi.LoadTest`) that simulates a **busy Friday at Mau's**:
- 4 terminals and 12 waiters
- **3 tickets opened per minute per terminal**, each with **3 rounds** of 2–5 items and a Print each round
- voids in 3% of tickets, approvals by request
- settles with mixed payments
- all over **6 hours**, at **3× speed** in CI (a 2-hour run) and 1× locally

It uses the real HTTP and SignalR APIs, with the File printer.

**Targets**, measured and written to `docs/PERFORMANCE.md`:
- **Add a line:** p95 under **150 ms**.
- **Print:** p95 under **300 ms** until the job is queued, and under **1 s** until the File printer has written it.
- **Settle:** p95 under **400 ms**.
- **Table map refresh after a change:** under **500 ms** on every other terminal.
- **Errors:** **no** lost or duplicate lines, prints, receipts, payments or journal entries. After the run, a checker compares tickets, print jobs, payments, stock movements and the ledger, and the trial balance must be 0.
- **Memory:** the server's memory is stable, with growth under 10% over the last 4 hours.
- **Database:** size per 1,000 tickets, written down for planning disk space.

Run it on **SQL Server Express** (the Windows CI runner) and on PostgreSQL. Fix anything that misses its target, and report what was changed.

## Part C — Failure testing ("what if…")

Write an automated test, or a written manual test where automation isn't practical (in `docs/FAILURE-TESTS.md`, with steps and the expected result), for each of these:

1. **The server restarts mid-service.** Terminals show "Not connected", reconnect by themselves, and no line, print or payment is lost or doubled. Print jobs waiting in the queue still print, in order.
2. **The network drops on one terminal** while it's adding items. Queued taps either all arrive in order or show as refused. None are silently lost.
3. **A printer is off or out of paper.** Jobs fail, the banner shows, Retry works, and the bill still comes out before the dispense slip.
4. **The database is unavailable for 30 seconds.** The API returns a clear 503, terminals show "Server busy, retrying", and everything recovers when the database is back.
5. **The disk is full.** Writes fail cleanly, with no half-written tickets. A banner shows on the admin site, and backups report the failure.
6. **Power cut on the server.** After a restart, the database is consistent and every settled ticket has its journal entry. There's a UPS recommendation in INSTALL.md.
7. **The clock is wrong** on a terminal. Server time is used everywhere, and the terminal's clock doesn't matter.
8. **Two cashiers settle the same ticket,** or **two supervisors approve the same request** (already tested; link the tests here).
9. **ERPNext is down for a whole day.** The queue grows, nothing else is affected, and everything is sent once it's back.

## Part D — Training mode

Staff need to practise without touching real sales.

- **Turning it on:** **Settings → Training mode** (needs `settings.manage`) turns training on for **chosen terminals**.
- **On a training terminal:**
  - A **striped orange band** across the top reads "TRAINING · not real sales".
  - Every slip prints "TRAINING · NOT A RECEIPT" at the top and the bottom.
- **Where the data goes:** tickets from training terminals go to a **separate training work period**. They're excluded from every report, the ledger, stock, cash-ups and ERPNext. Store them with `IsTraining = true`. Tests prove they never reach those places.
- **Clearing:** **Clear training data** deletes them, and is audited.
- **For new hires:** they can use training mode before going live, with **demo-style practice tables**.

## Part E — Pilot plan at Mau's Village (docs)

Write `docs/PILOT-MAUS.md` for the owner and NexaGen. It describes, step by step:

1. **Week 0, setup:**
    - Install the server on **POSSERVER2**, using the existing SQL Server Express instance with a **separate `TausiPOS` database**. Nothing in SambaPOS is touched.
    - Import the menu, tables and staff from SambaPOS (Task 14).
    - Set a fixed IP, and check the backups and the second copy.
    - Install terminals **alongside** SambaPOS on Counter A first.
2. **Week 1, training:**
    - Training mode on Counter A.
    - Each staff member practises: ordering with Cold/Warm, print, void with a request, settle with M-Pesa and cash, cash-up.
    - A checklist per person, to sign off.
3. **Week 2, parallel run on one counter:**
    - Counter A uses **Tausi POS for real** for one shift a day. The other counters stay on SambaPOS.
    - **Compare each night:** sales, the payments total and the M-Pesa codes against the till statement, and stock for the counted items. Use the **comparison sheet** in the doc.
    - Log every problem in `docs/PILOT-LOG.md` (date, what, who, severity), and fix it as a patch release.
4. **Week 3, all counters:**
    - All four terminals on Tausi POS.
    - SambaPOS stays installed but unused, **as a fallback**. The doc has a clear "if Tausi POS is down" procedure.
    - The first full stock count, and the first full cash-up week.
5. **Week 4, sign-off:**
    - The owner signs off when there have been 7 days in a row with no Severity 1 issues, cash-ups balance, and the stock variance is within the agreed limit.
    - Then remove the fallback.

The doc also includes:
- **Severity definitions:**
  - **Sev 1:** service stops, or money or stock is wrong.
  - **Sev 2:** a workaround exists.
  - **Sev 3:** cosmetic.
- **Response times** for NexaGen.
- **A daily check:** backup OK, no failed prints, the ERPNext queue empty (if it's on).

## Part F — User guides (short, printable)

In `docs/guides/`, each guide is **one or two A4 pages**, in plain words, with screenshots taken automatically by a Playwright script (`tools/screenshots`) against the demo data. Include a PDF version of each.

- **Waiter guide:** log in, tables, order, Cold/Warm, Print, leaving unprinted items, voids and requests, My sales, log out.
- **Cashier guide:** start a shift, settle (cash with change, M-Pesa codes, card, split payments), "brought by", reprint receipt, pay out, cash drop, the blind count, closing the shift.
- **Supervisor guide:** approvals on their own counter and by PIN, voids and gifts, close day and what blocks it, the Z report.
- **Owner/admin guide:** menu and prices, staff and PINs, permissions, printers and terminals, stock (deliveries, transfers, counts), the cash book, reports, backups, the licence, updates.

## Part G — Version 1.0.0

- **Version numbers:** set the version to **1.0.0** everywhere (server, terminal, installers, `/health`, the About line).
- **`CHANGELOG.md`:** a 1.0.0 entry summarising v1's features in plain words.
- **Release notes draft:** `docs/RELEASE-1.0.0.md`, with what's in v1, the system requirements, the known limits (from the "After v1" list), and how to upgrade from pilot builds.
- **THIRD-PARTY-NOTICES:** make it complete for every shipped dependency, and add the **full licence texts** to the installers, from the follow-up in R1.
- **LICENSE:** confirm the wording matches what the owner approved.
- **Tags:** **Don't tag or publish.** The owner tags `v1.0.0`, which triggers the release workflow (Task 15).

## Part H — Tests

- **Load test:** it runs in CI (shortened) and fails on a missed target or any consistency error.
- **Failure tests:** the automated Part C tests pass.
- **Dev paths:** each one is refused outside Development.
- **Training mode:** training data never appears in reports, the ledger, stock, cash-ups or ERPNext, and Clear works.
- **Business rules:** every rule in `AGENTS.md` is linked to a test in `V1-CHECKLIST.md`.
- **Screenshots:** the script runs in CI and produces the guide images.

## Acceptance checklist

- [ ] `docs/V1-CHECKLIST.md` covers every design section and wireframe, the small Missing items are fixed, and the larger ones are listed for my decision.
- [ ] The load test meets every target on SQL Server Express and PostgreSQL, the after-run checker finds 0 differences, and `docs/PERFORMANCE.md` has the numbers.
- [ ] Every Part C scenario has a passing test, or a written manual test I can follow.
- [ ] Training mode on Counter A shows the orange band and prints TRAINING, and none of it reaches reports, stock, the ledger or ERPNext.
- [ ] `docs/PILOT-MAUS.md` and the four guides (with PDFs and screenshots) are ready to print.
- [ ] The version is 1.0.0 everywhere, and the CHANGELOG, release notes and THIRD-PARTY-NOTICES are complete.
- [ ] All tests, audits and CI pass. Push `release/17-v1-hardening` and stop.

When done, report as `AGENTS.md` describes, with the list of larger Missing items needing my decision, the performance numbers, and anything you decided differently from this prompt.
