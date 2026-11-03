# Tausi POS performance

Measured for version 1.0.0 with the load test (`tools/Tausi.LoadTest`, how to run it: RUNNING.md, "Load
test"). The full run is by hand from the Actions tab (`load-test.yml`).

## The busy Friday

A four-counter bar: 12 waiters on four terminals, a fifth terminal watching the table map, 6 hours of
service at about 720 tickets an hour, about three prints per ticket, some voids, and every ticket
settled in cash, M-Pesa or both. The run plays the 6 hours
at 3× speed (2 real hours) against one server, with prints going to the File printer.

Where: GitHub's hosted runners, with the server, the database and the load test on one machine (4 CPUs,
16 GB): Ubuntu with PostgreSQL 16, and Windows with SQL Server Express. Run 41344276181, 1 Nov 2026, on
the code of `release-15-v17`.

## Results

95% of requests finished within (p95):

| Measure | Target | PostgreSQL | SQL Server Express |
| --- | --- | --- | --- |
| Add a line | under 150 ms | 36 ms | 61 ms |
| Print, until queued | under 300 ms | 81 ms | 84 ms |
| Print, until the slip was written | under 1000 ms | 81 ms | 418 ms |
| Settle | under 400 ms | 31 ms | 57 ms |
| Table map on another terminal | under 500 ms | 84 ms | 111 ms |
| Tickets settled | | 4,324 | 4,320 |
| Prints | | 12,972 | 12,960 |

On both: **every target met, nothing lost or doubled** (every ticket, line, print, payment and void the
test made is in the database exactly once), and the ledger balanced (debits equal credits).

Server memory over the last 4 hours: PostgreSQL 314 → 321 MB (+2%); SQL Server 91 → 62 MB (Windows
trims a service's working set, so this figure shows no leak but not much more).

## Database size

What a run adds is mostly short-lived. Saved answers for retried requests (`IdempotencyRecords`) are
deleted after 24 hours, and the text of printed slips is cleared after `print.keepSlipTextDays`. What
stays is the tickets, lines, payments, stock movements, journal entries and the audit log.

From 360-ticket runs that list each table (run 41594948958, 3 Nov 2026):

| | PostgreSQL | SQL Server Express |
| --- | --- | --- |
| Everything after the run, per 1,000 tickets | 60 MB | 226 MB |
| Of which saved answers (gone after 24 h) | 30 MB | 160 MB |
| Of which print jobs (slip text cleared later) | 13 MB | 31 MB |
| **What stays, per 1,000 tickets** | **about 18 MB** | **about 34 MB** |

SQL Server counts only the pages its data uses (not the log or free space). Its pages are about 60–70%
full, because ids made in time order land all over SQL Server's index order; text also takes 2 bytes a
character there.

**SQL Server Express stops a database at 10 GB.** At about 34 MB per 1,000 tickets (65 MB if print jobs
are counted in full), a bar doing 250 tickets a day reaches it in about 1.7 to 3.3 years, one doing 120 a
day in about 3.5 to 7 years. The saved answers add up to one busy day's worth on top (about 0.7 GB for
the 4,320-ticket Friday). PostgreSQL has no such limit.

## Not measured

- Real counter PCs, printers and networks: the runs are on GitHub's machines with the File printer. The
  pilot sites are the check for those.
- More than one day: the runs are 6 hours of service.
