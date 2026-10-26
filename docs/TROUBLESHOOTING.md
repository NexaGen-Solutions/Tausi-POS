# Troubleshooting Tausi POS

For the bar owner and the NexaGen technician. Start at the problem you see.

## A terminal says "Not connected"

The counter PC can't reach the server. Check, in this order:

1. **The network**: is the counter PC's cable plugged in (or its Wi-Fi on)? Can it open any page on the
   server, for example `http://<server address>:5080/health` in Edge? It should show `"status":"ok"`.
2. **The server PC is on** and not asleep. Set it never to sleep: Settings → System → Power → Screen and
   sleep → Never.
3. **The service is running**: on the server, press Win+R, type `services.msc`, find **Tausi POS Server**.
   Its status should be *Running*; if not, right-click → Start. If it stops again, see the logs below.
4. **The firewall**: on the server, Windows Defender Firewall → Advanced settings → Inbound Rules →
   **Tausi POS Server** must be enabled, and the network must be *Private* (Settings → Network →
   Ethernet → Network profile type → Private). The rule only covers private networks.
5. **The server's IP address changed** (the router gave it a new one): the terminals still look at the
   old one. Find the new one (on the server: Win+R → `cmd` → `ipconfig`), then give the server a **fixed
   IP** (see INSTALL.md) — or on each terminal choose **Change server** and type the new address.

The terminal keeps trying by itself and reconnects within seconds once the server is back. Orders
already sent are on the server.

## Printing

**"Not printed · Retry" on a terminal**: the slip didn't reach the printer; the order itself is saved.

- Is the printer on, with paper, and its cover closed?
- Network printers: can the server reach it? Admin site → Terminals and printers → the printer →
  **Test print**. If it fails, check the printer's IP address (print its self-test page: hold the feed
  button while switching it on) against the one in Tausi POS.
- USB printers (plugged into a terminal): that terminal must be on and connected.
- Then press **Retry**.

**It prints on the wrong printer**: Admin site → Terminals and printers: check which printer each
terminal's bills go to, and which printer each menu category's slips go to.

**Text is cut off or wraps strangely**: the paper width is wrong. Press **Ruler test** on the printer: the
ruler should reach the paper's edge. Set 58 mm or 80 mm to match.

## Where the logs are

- **The server's own log files**: `C:\ProgramData\Tausi POS\logs\tausi-<date>.log` (one a day, kept
  30 days). Open them as administrator. Linux: `/var/log/tausi/` and `journalctl -u tausi`.
- **Windows Event Viewer**: errors are also written there.
  1. Press Win+R, type `eventvwr.msc`, Enter.
  2. Open **Windows Logs → Application**.
  3. On the right, **Filter Current Log…** → Event sources: **Tausi POS** → OK.
  The newest errors are at the top; double-click one for the details.
- **The installer's log**: `%TEMP%\Tausi_POS_Server_*.log` (Win+R, type `%TEMP%`).
- Settings → **System** on the admin site shows the server's health at a glance: database, free disk
  space, last backup and licence.

## A backup failed

The admin site and the Close day screen show a red banner: "Last night's backup failed", "No backup has
worked for 2 days", or "The backup copy failed".

1. Open Settings → **Backups** and press **Back up now**. The message says what went wrong.
2. **"The backup copy failed"**: the backup on the server worked, but the second copy didn't reach its
   folder. Plug the USB drive back in, or check the network share is on; the next backup copies again.
3. **Disk full**: Settings → System shows the free space. Free some space on the server's disk (old
   downloads), or move older backups elsewhere.
4. **The database isn't reachable**: see the service and SQL Server: `services.msc` → **SQL Server
   (TAUSI)** (or SQLEXPRESS) must be running.
5. Still failing: send NexaGen the newest log file (above).

## An update failed

The update takes a backup first. If the new version can't update the database or doesn't start, **the
installer puts the previous version and its data back by itself**: the bar keeps working on the version
it had. Nothing is lost.

- The installer's log: `%TEMP%\Tausi_POS_Server_*.log`, and the update's own log in
  `C:\ProgramData\Tausi POS\logs\update-<date>.log`.
- Settings → System shows the version that is running.
- Send both logs to NexaGen. The backup taken before the update is in Settings → Backups ("Before an
  update"), in case it is ever needed.

A **terminal** that fails to update keeps working on its current version and tries again at its next
start.

## A lost admin password

Someone with access to the **server PC** can set a new one:

1. On the server, open a Command Prompt **as administrator** (Win, type `cmd`, right-click → Run as
   administrator).
2. Run:

   ```bat
   "C:\Program Files\Tausi POS\Server\TausiServer.exe" reset-admin
   ```

   It prints a **reset token** (like `K7QM-3HXP`), which works for one hour. Linux:
   `sudo -u tausi env $(cat /etc/tausi/tausi.env | xargs) /opt/tausi/TausiServer reset-admin`.
3. In a browser, open `http://<server>:5080/setup/recover`, type the token, the admin's user name (or
   leave it empty for the first admin) and a new password (at least 10 characters).

The reset is recorded in the audit log. Five wrong tokens lock it for 5 minutes.

A **forgotten PIN** is simpler: an admin or supervisor resets it on the admin site (Staff and PINs).

## Too many wrong tries

- **Admin site**: 10 wrong passwords in 10 minutes lock that user name, and the PC they came from, for up
  to 10 minutes.
- **Terminal PIN pad**: 5 wrong PINs lock that terminal for 1 minute.

Wait, then try again. Repeated lock-outs are in the audit log (Reports → Audit log).

## "Server is restoring a backup"

Someone restored a backup on Settings → Backups. The terminals wait and come back by themselves when it
is done (usually under a few minutes). Don't switch the server off meanwhile.
