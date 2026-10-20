# Task 4 — Admin: Terminals and printers

Read `AGENTS.md` first. Use the wireframe **"17 · Admin: terminals and printers"** in `docs/wireframes/`. Work on a new branch `screen/04-admin-terminals` from `main` (tag `screen-03-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This task is where real hardware comes in. Build everything so it can be tested with the **File** transport on Ubuntu. I will then test on a real Windows counter PC with a real 80 mm thermal printer, so `docs/RUNNING.md` must include a short **"Testing on real hardware"** section (see Part H).

---

## Part A — Terminals list (`/admin/terminals`)

Access needs `devices.manage`. Match the top table of wireframe 17.

**Columns:**
- **Terminal** name
- **Status:** **Online** (green), **Offline** (orange, with "last seen 3 min ago") or **Revoked** (grey)
- **Current user** (or "—")
- **App version**
- **Price list**
- **Dispense printer**
- **Receipt printer**
- **Edit**

**Online status:**
- The terminal app's SignalR connection is the heartbeat. A terminal counts as online while it is connected, and offline 30 seconds after disconnecting.
- Publish `terminal.status` so the list updates live.

**Edit terminal** (right panel):
- name
- price list (uses the endpoint from Task 3)
- **dispense printer**, plus the **dispense header** text printed at the top of dispense slips (e.g. "Counter A")
- **receipt printer** (the bill and the final receipt)
- active

**Routing preview** in the panel, built from the current settings:
> **Print on Counter A:** 1. Bill → Ticket Printer 1 · 2. Dispense slip "Counter A" → Ticket Printer 1

At Mau's, Pillar 1 should be able to send its bill to Ticket Printer 1 and its dispense slip to the Pillar 1 printer. Keep the existing template model from earlier tasks, but make sure any printer can be chosen for either role, and the header text is editable per terminal.

## Part B — Pairing from the admin site (replaces curl)

- **Pair a terminal** opens a panel: enter the 6-digit code shown on the new terminal, then choose an existing unpaired terminal record or **create a new terminal** (name required).
- **Codes:** they expire after 10 minutes and are single-use. A wrong code shows "Code not found or expired". Five wrong codes from one admin session in 5 minutes block further tries for 5 minutes.
- **On success:**
    - the terminal app receives its device token
    - it shows "Paired as Counter A" and goes to the PIN screen
    - the pairing is audited
- **Re-pairing:** pairing an already-paired terminal name revokes the old device token first, after a confirmation saying the old device will be logged out.
- **Revoke:** sets the terminal to Revoked and invalidates its token. If the app is running, it returns to its pairing screen within 2 seconds.
- **Licence limit:** count active (paired, not revoked) terminals. If a licence with a terminal limit exists and the limit is reached, pairing is refused with "Your licence allows N terminals." With no licence record (dev), don't limit; note this in DECISIONS.md.
- **Retire the old dev pairing endpoint**, and update RUNNING.md.

## Part C — Printers list and setup

Match the bottom table of wireframe 17.

**Columns:**
- **Printer**
- **Connection:** `Network · 192.168.1.50:9100`, `Windows share · \\COUNTER-A\TP1`, `Via terminal (USB) · Pillar 2`, or `File (dev)`
- **Used by:** which terminals use it, and as what
- **Paper:** 58 / 80 mm
- **Status:** Ready / Error (with the last error in a tooltip and on tap) / Unknown
- **Last printed**
- actions: **Test print**, **Edit**, **Failed jobs (n)**

**Add / edit printer:**
- name
- connection mode
- address, depending on the mode:
    - Network: IP and port, default 9100
    - Windows share: UNC name
    - Via terminal: the owning terminal
- paper width
- **Cut paper after each slip** (on by default)
- **Open cash drawer on receipt** (off by default; sends the ESC/POS drawer pulse when a final receipt prints)
- active

**Check connection** button:
- Network: opens a TCP connection to the IP and port (2-second timeout)
- Windows share: checks the printer exists, and only when the server runs on Windows
- Via terminal: asks the owning terminal

It shows "Reachable" or the error in plain words, such as "No answer from 192.168.1.50:9100 — is the printer on and plugged into the network?"

**Status upkeep:**
- **Network printers:** checked every 60 seconds in the background.
- **Every printer:** updated after each job (Ready on success, Error with the message on failure).
- **Live updates:** publish `printer.status`.

**Not in use:** a printer used by a terminal can't be deactivated until it's replaced on that terminal. Show which terminals use it.

## Part D — Test print and the slip layouts

**Test print** prints:
- the business name, logo (if set) and the printer name
- the connection and paper width
- a **column ruler** (`1234567890…` to the full width: 42 characters for 80 mm, 32 for 58 mm)
- a **bold line**, a **double-height line** and a centred line
- "Bill and dispense slips will look like this:", then a short sample bill (2 items, a total, the "Total includes VAT 16%" line), a cut, and a short sample dispense slip with the dispense header
- the date, time and "Test OK", then a cut

It is queued like any job, so it shows up in Failed jobs if it fails. Audit it.

Put the slip templates (bill, dispense, void, receipt, cash-up, test) in one place, and render the bill and dispense samples from the **same code** the order screen will use later, so the test print is a true preview.

## Part E — Failed jobs and retry

- **Failed jobs panel** per printer: each failed job shows time, kind (Bill / Dispense / Void / Receipt / Test), the related ticket (once tickets exist), the error, and the attempts.
- **Actions:** **Retry**, **Send to another printer** (choose any active printer; useful when a printer dies mid-shift), and **Discard** (asks for a reason, audited). There's also **Retry all**.
- **Order is kept:** retried jobs keep their place, so a bill always prints before its dispense slip, even on a retry.
- **API:** `GET /api/admin/printers/{id}/jobs?status=failed`, `POST /api/admin/print-jobs/{id}/retry`, `/redirect`, `/discard`. The POS "Not printed · Retry" banner comes with the order screen and will use these.

## Part F — Real printing on Windows

1. **Terminal app (Tauri, Rust): implement the `print_raw` command** that the foundation left as a stub.
    - **Windows:** send the raw ESC/POS bytes to a named local printer using the Windows spooler in RAW mode (`OpenPrinter` / `StartDocPrinter` with datatype "RAW" / `WritePrinter`, via the `windows` crate).
    - **Linux (for dev):** pipe to `lp -d <printer> -o raw`, or write to a file when the printer name is `file:`.
    - **Errors** are reported back to the server with a clear message.
    - **Local printer list:** the terminal reports its installed printers (names) to the server on connect, so the admin can pick the Windows printer name from a dropdown for "Via terminal" printers instead of typing it.
2. **Server, `WindowsShare` transport:** when the server runs on Windows, print to `\\PC\Share` using the same RAW spooler approach (P/Invoke to winspool, in Infrastructure). On Linux it returns "Windows share printing needs the server to run on Windows" (already the behaviour; keep the message).
3. **Logo printing:** the business logo is converted once to a 1-bit raster sized for the paper width and cached. It prints on bills and receipts when a setting `print.logoOnBill` is on (default on).

## Part G — Lock down the terminal app

- **Network access:** restrict the Tauri app's permissions and CSP so it can only talk to the **paired server's address** (plus the update address once there is one). This closes the foundation follow-up.
- **Server address changes:** possible only from the terminal's setup screen, which asks for an admin-site login first.

## Part H — Docs: testing on real hardware

Add a section to `docs/RUNNING.md`:

1. Build the terminal installer with the release workflow, or locally on a Windows PC.
2. Install it on a Windows 10/11 counter PC on the same network as the dev server, and pair it from `/admin/terminals`.
3. Set up a printer:
    - **USB:** "Via terminal", picking the Windows printer name from the list.
    - **Network:** the IP, then "Check connection".
4. Run a test print and check the column ruler fits exactly on one line (if not, the paper width is wrong).
5. Unplug the printer, test print, and see the job in Failed jobs. Plug it back in, Retry, and it prints.

## Part I — Tests

**Server:**
- Pairing:
    - success
    - wrong code
    - expired code
    - code reuse
    - the rate limit
    - re-pairing revokes the old token
    - revoke ends access
    - the licence limit (with and without a licence)
- Online/offline status from connect/disconnect events with a fake clock.
- Printer validation: the address is required per mode, and a printer in use can't be deactivated.
- Check connection for Network against a fake TCP listener: reachable, refused and timeout.
- The test print content at 42 and 32 columns. The ruler length matches the paper width exactly.
- Failed jobs:
    - retry keeps the order
    - redirect sends to another printer
    - discard needs a reason and is audited
- The cash drawer pulse bytes are added only to final receipts, and only when enabled.
- The routing preview text.

**Rust:** unit tests for building the print request, and for error mapping on non-Windows.

**Web:**
- The terminal list updates live on status.
- The pairing panel: success, wrong code and the rate-limit message.
- The printer form fields per mode, and Check connection results.
- Failed jobs actions.

## Acceptance checklist

- [ ] Pair a new browser terminal from `/admin/terminals` using its code. No curl is needed. Revoke it, and it returns to its pairing screen within 2 seconds.
- [ ] The terminals list shows Online/Offline live as terminals connect and disconnect, with the current user.
- [ ] Set Pillar 1's receipt printer to Ticket Printer 1 and its dispense printer to Pillar 1. The routing preview reads correctly.
- [ ] A test print (File transport) shows a ruler exactly 42 characters wide on 80 mm and 32 on 58 mm, plus a sample bill and dispense slip.
- [ ] Point a Network printer at an address with nothing listening. Check connection gives a plain-language error, a test print lands in Failed jobs, and "Send to another printer" prints it on the File printer.
- [ ] The terminal app's Windows build compiles in CI with the real `print_raw`. (I'll test on the real printer using Part H.)
- [ ] The terminal app can't reach any address other than its server.
- [ ] All tests, lint and CI pass. Push `screen/04-admin-terminals` and stop.

When done, report as `AGENTS.md` describes, including what I need to test on the real Windows PC and printer.
