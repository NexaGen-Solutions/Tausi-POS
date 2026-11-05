# Tausi POS 1.0.1 — release notes (draft)

> Draft for the owner. The owner merges `release/1.0.x`, tags `v1.0.1`, publishes the draft release and
> uploads the files to the update site, as for 1.0.0.

## Before anything else: update each terminal by hand, once

1.0.1 fixes how terminals install updates, so terminals on 1.0.0 can't take it safely by themselves.

1. Update the **server** first (`TausiPOS-Server-Setup-1.0.1.exe`, or Settings → System). It takes a
   backup first, then moves the sales already recorded to their calendar dates.
2. On **each counter PC**, run `TausiPOS-Setup-1.0.1.exe` once (choose **Yes**). It keeps the pairing.
3. On the admin site, **Terminals and printers** shows each terminal's version and its last update.

From 1.0.1 on, terminals update from the server by themselves again.

## What's fixed

- **Sales count on the day they happened**, not the day their work period was opened. Items count on the
  date they were served, payments on the date they were taken; the day changes at midnight in the
  business's time zone. Reports open on **Today**; work periods are a second choice for Z reports and
  cash-ups. A Z report for a work period that ran over several days shows each day, with a warning.
- **A terminal app can't vanish after a failed install.** The installer keeps the previous version, and a
  launcher starts the app, restarts it, goes back to the previous version if a new one doesn't start,
  and offers **Repair** from the server, keeping the pairing.
- **The Terminals page warns** when a terminal's last update failed or was rolled back, or the app
  stopped unexpectedly.

## Things to know

- **Earlier days' figures may move.** Sales from 1.0.0 move to the dates they actually happened, so a
  day's report can differ from what it showed before the upgrade. Each journal entry keeps its old date
  for this release, so the change can be checked. Z reports printed earlier aren't changed.
- **Unsettled sales** is a new ledger account: items served on one day and paid on a later one sit there
  overnight. It's zero once everything is paid.
- Reminders or automatic closing for work periods left open for days aren't in this version.
