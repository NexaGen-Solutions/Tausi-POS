# Task 15 — Installers, Windows service, backups and automatic updates

Read `AGENTS.md` first. There's no wireframe for this task, apart from the small admin pages described below, which use the existing admin layout. Work on a new branch `release/15-installer` from `main` (tag `screen-14-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This task makes Tausi POS installable by a customer **without a developer**, on the hardware a bar actually has:
- a **server installer** that sets up the database, the Windows service and the firewall
- the **terminal installer**, finished, with automatic updates
- **backups and restore** that a non-technical owner can trust
- a **Linux server package**, for NexaGen's own hosting and for dev

The design says the server runs on Windows or Linux, and that terminals are Windows 10/11.

---

## Part A — The server on Windows

**Publishing:** publish `Tausi.Api` as a **self-contained, single-folder** `win-x64` build, with the web UI's static files included. Self-contained means no separate .NET runtime install.

**Windows service:**
- Run it with `UseWindowsService()`. The service name is `TausiPOSServer` and the display name "Tausi POS Server", as in R1.
- Start type: **Automatic (Delayed Start)**.
- Recovery: restart after 1 minute on the first, second and later failures.
- It runs as **LocalService**, or as a dedicated virtual account (`NT SERVICE\TausiPOSServer`), not as LocalSystem.

**Folders:**
- **Program files:** `C:\Program Files\Tausi POS\Server\`
- **Data:** `C:\ProgramData\Tausi POS\`, holding `config\`, `data\` (logo, uploads, attachments, PIN pepper, data-protection keys), `logs\`, `backups\` and `print-output\` (dev only).
- Give the service account write access to the data folder only.

**Configuration:**
- It lives in `C:\ProgramData\Tausi POS\config\appsettings.Production.json`, and survives updates.
- **Secrets** (the database password and so on) are protected with **DPAPI** (machine scope) and never stored as plain text.
- **Port:** default **5080**, which can be changed in the installer.

**Logs:**
- Rolling daily files in `logs\`, kept for 30 days.
- Errors also go to the **Windows Event Log** (source "Tausi POS"), so the owner's IT person finds them where they'd expect. That would have helped with the SambaPOS log hunt.

**Health:**
- `GET /health` returns version, database status, disk space free, last backup, and the licence state.
- The admin site shows these on a new **Settings → System** page.

## Part B — The server installer (Windows)

Build `TausiPOS-Server-Setup-<version>.exe` with **WiX Toolset v5** (MSI inside a bundle). Add WiX to THIRD-PARTY-NOTICES. CI builds and signs it (Part F).

**Installer pages:**
1. **Welcome:** "Tausi POS Server by NexaGen Solutions", the version, and the licence agreement (the LICENSE text).
2. **Database:**
    - **Install SQL Server Express** (the recommended choice):
        - The bundle downloads and runs the **SQL Server 2022 Express** installer silently, with a named instance `TAUSI`, Windows authentication, and TCP enabled on the local machine only.
        - If the PC has no internet, it asks for the SQL Express installer file to be chosen from a USB drive.
    - **Use an existing SQL Server:** a server or instance name, then authentication, then **Test connection**. Mau's already has SQL Server Express on POSSERVER2 for SambaPOS. Tausi uses its **own database** (`TausiPOS`) on that instance, and **never** touches the SambaPOS database.
    - **Use PostgreSQL:** for advanced users. Host, port, user, password, and Test.
3. **Port:** 5080 by default, checked to be free, with **Open the Windows Firewall for the local network** ticked. The rule allows the private profile only, for the port, named "Tausi POS Server".
4. **Install:** copies the files, writes the config, creates the database and runs migrations, installs and starts the service, and waits for `/health`.
5. **Finish:**
    - Shows the **server address** for terminals, for example `http://192.168.1.10:5080`, with every LAN address listed.
    - Shows the **setup token** (Task 14), and a reminder to set a fixed IP for the PC.
    - Has a button: **Open setup in the browser**.

**Upgrades:**
- Running a newer installer **upgrades in place**: it stops the service, **takes a backup first** (Part C), replaces the files, runs migrations, starts the service again, and checks health.
- **If migrations or health fail,** it **restores the backup and the previous files automatically**, and shows the error with the log location.
- Config and data are never overwritten.

**Uninstall:** it removes the service, the program files and the firewall rule. It **keeps** `C:\ProgramData\Tausi POS\` and the database, unless the user ticks "Also delete all data", which asks for confirmation by typing the business name.

**Silent install** for NexaGen technicians: `TausiPOS-Server-Setup.exe /quiet DB=existing SQLINSTANCE=.\SQLEXPRESS PORT=5080`. Document it in `docs/INSTALL.md`.

## Part C — Backups and restore

**Automatic backups:**
- **Nightly** at a set time (default **05:00** business time, after closing), and **before every upgrade and restore**.
- **SQL Server:** `BACKUP DATABASE … WITH COMPRESSION, CHECKSUM` (with compression only where the edition supports it). **PostgreSQL:** `pg_dump -Fc`.
- **The data folder too:** each backup also zips the important parts of the data folder (logo, uploads, PIN pepper, data-protection keys, config), excluding logs.
- **Without the keys, a restored database can't be used,** so the keys are always part of the backup.
- **One file:** each backup is a single `.tausibak` file (a zip with the database backup, the data files, and a `manifest.json` holding the version, schema version, business name, date and checksums).
- **Optionally encrypted** with a password set by the owner, using AES-256. The owner is warned that a lost password means a lost backup.

**Where they go:**
- `C:\ProgramData\Tausi POS\backups\` by default.
- **Plus a second copy** to a folder the owner chooses: a USB drive, a network share, or a synced folder such as OneDrive or Google Drive.
- **Keep:** the last 14 nightly backups, plus the first backup of each month for 12 months.

**Checks:**
- After each backup, it's **checked**: the file opens, the checksums match, and for SQL Server `RESTORE VERIFYONLY` runs.
- A failure shows a red banner in the admin site and on the Close day screen: "Last night's backup failed". It also appears in `/health` and the Event Log.
- No successful backup for **48 hours** shows the same banner.

**Admin page** (**Settings → Backups**, needing `settings.manage`):
- the schedule and the second-copy folder
- encryption on or off
- **Back up now**
- the backup list, with date, size, verified, and where it's copied to
- **Download** for one backup
- **Restore**

**Restore:**
- From the admin page, or from a command (`TausiServer.exe restore <file>`) for when the admin site won't start.
- It stops the POS (terminals see "Server is restoring a backup"), takes a safety backup of the current state, restores the database and the data files, runs migrations if the backup is older, and starts again.
- **Restoring a backup from a different business name** needs an extra confirmation.
- Every restore is audited (the audit survives in the restored database as a new entry).

**Moving to a new PC:**
1. Install the server on the new PC.
2. Choose **Restore from a backup** on the installer's Database page, which skips setup.
3. Point the terminals at the new address. Make this one action in the admin site, through the terminal's setup screen (Task 4 Part G).

Document it in `docs/INSTALL.md`.

## Part D — The terminal installer, autostart and kiosk

The terminal installer from Task 4 CI becomes the finished `TausiPOS-Setup-<version>.exe` (NSIS or MSI through Tauri). It contains:
- **WebView2:** the Evergreen bootstrapper, installed if it's missing, with an offline option.
- **Per-machine install** to `C:\Program Files\Tausi POS\Terminal\`.
- **Autostart for every user,** using the Tauri autostart plugin or a machine-wide Run key. It opens **full-screen**.
- **Kiosk mode setting** (on by default):
  - Full-screen with no window frame. Alt+F4 and closing are blocked.
  - **Exiting** needs a hidden gesture (five taps in the top-left corner) **plus an admin-site login**, so cashiers can't leave the POS but a technician can.
  - Document how to turn it off.
- **The pairing screen** on first start (Task 4). It saves the server address, and the device token is stored with DPAPI.
- **Silent install:** `TausiPOS-Setup.exe /S SERVER=http://192.168.1.10:5080`, which pre-fills the address.

## Part E — Automatic updates (terminal and server)

**Terminal app:** use the **Tauri updater plugin**, with signed updates.
- **Where updates come from:** the terminal checks **its own server** (`/api/updates/terminal/latest.json`) instead of the internet, so bars without internet still update. **The server is the update source for the whole bar.**
- **When updates install:**
  - It checks on start and every 6 hours.
  - It downloads in the background.
  - It **installs only when the PIN screen is showing and no one has been logged in for 5 minutes**, or at the next start. It never installs mid-order.
- **Rule: server first.** The terminal never updates to a version **newer** than its server. The server publishes the terminal version that matches it.

**Server updates:**
- **Settings → System** shows the installed version and **Check for updates**. That checks NexaGen's update feed (Part F) over HTTPS, if the server has internet.
- **Apply update:**
  - It's **only allowed when the day is closed**, or by forcing it with a typed confirmation.
  - It downloads the server installer, checks its signature, and runs it silently.
  - The Part B upgrade path applies: backup, migrate, roll back on failure.
- **Offline bars:** a technician can upload an update package from a USB drive on the same page.
- **Updating terminals with the server:** after the server is updated, it starts offering the matching terminal version, so the terminals follow.

**Version compatibility:**
- Every API call sends `X-Tausi-Version`.
- A terminal that's **older** than the server's minimum supported terminal version shows "Updating, please wait" and updates before login.
- A terminal **newer** than the server never happens, because of the server-first rule.
- Add a test that runs the previous terminal version against the new server, using a contract test of the API.

## Part F — Release pipeline (GitHub Actions)

**The workflow:** `release.yml` runs on tags `v*`. Version numbers follow `MAJOR.MINOR.PATCH`, taken from the tag, and flow into every assembly, the installer, `tauri.conf.json` and `/health`.

**It builds:**
1. the server for `win-x64` (self-contained), with its WiX installer
2. the server for `linux-x64`, as a `.tar.gz` with a systemd unit and an install script (Part G)
3. the terminal installer, with the updater signature
4. `Tausi.LicenseTool` (for NexaGen internal use, kept out of public release assets)

**Signing:**
- **Authenticode signing** of the Windows `.exe` and `.msi` files, using a code-signing certificate from GitHub secrets. Without signing, Windows SmartScreen warns on install.
- If no certificate is set, the build still works and marks the artifacts "unsigned". Record that a certificate must be bought before selling, in DECISIONS.md.
- The **Tauri updater** signing key also comes from secrets.

**Outputs:**
- A **GitHub Release**, a draft, with the artifacts, the SHA-256 checksums, and the release notes built from the conventional commits since the last tag.
- An **update feed** (`updates.json`) with the version, minimum terminal version, URLs, signatures and the notes, published with the release, for servers to check.

**Rule:** the owner publishes the draft release. Codex never publishes a release or pushes a tag (as `AGENTS.md` already says).

## Part G — Linux server package

- **The tarball** contains the published server, `tausi.service` (systemd, with a `tausi` user, restart always, and the environment file `/etc/tausi/tausi.env`), and `install.sh`.
- **`install.sh`:**
  - creates the user and the folders (`/opt/tausi`, `/var/lib/tausi`, `/var/log/tausi`, `/etc/tausi`)
  - sets up PostgreSQL if it's asked to (`--with-postgres`, on Ubuntu 22.04 and 24.04), with a random password
  - opens the port in `ufw` for the local network only
  - starts the service
  - prints the address and the setup token
- **Backups** on Linux use the same Part C code, through `pg_dump`.
- **Docs:** document it in `docs/INSTALL.md`, with the warning: "Run the server on the bar's own network. Don't expose port 5080 to the internet."

## Part H — Security basics for a LAN product

- **Admin site behind a reverse proxy (optional):** an option to put the admin site behind HTTPS with a **self-signed certificate made at install** (shown with its fingerprint) for owners who want it. Leave it off by default for v1, because terminals connect over the LAN by IP. Record the trade-off in DECISIONS.md.
- **Login rate limits:** the admin login is locked after 10 wrong passwords in 10 minutes, per user and per IP. PIN logins are locked per terminal after 5 wrong PINs in a row, for 1 minute.
- **Security headers** on every response: CSP, `X-Frame-Options DENY`, `Referrer-Policy`, `X-Content-Type-Options`.
- **Privacy:** the admin and API never return PIN hashes, password hashes or the device token. Add a test that checks each DTO for those field names.
- **Dependency checks in CI:** `dotnet list package --vulnerable`, `npm audit --omit=dev`, and `cargo audit`. They fail on high or critical issues.

## Part I — Docs for the customer and technician

Write these in `docs/`, in plain language for a bar owner and a NexaGen technician:
- **`INSTALL.md`:** the hardware needed (server PC: 4 GB RAM, SSD, wired network, a UPS recommended; counter PCs: Windows 10/11, 4 GB RAM), a fixed IP, the server install, the terminal install and pairing, printers, backups and the second copy, moving to a new PC, updates, uninstall, and the silent install options.
- **`TROUBLESHOOTING.md`:**
  - **"Terminal says Not connected":** network, the server service, the firewall, the IP changed.
  - **Printer problems:** "Not printed", the wrong printer, the ruler test.
  - **Where the logs are,** with Event Viewer steps.
  - **A failed backup.**
  - **A failed update:** what the rollback did.
  - **A lost admin password:** a command on the server, `TausiServer.exe reset-admin`, that needs the setup token printed again locally.
- **`CHANGELOG.md`**, built by the release workflow.

## Part J — Tests

- **The service host:** it starts as a Windows service in CI on `windows-latest`, `/health` is OK, and it stops cleanly.
- **Installer** (CI on Windows):
  - a silent install with **existing SQL Server** (a LocalDB or SQL Express service container), then the service runs and `/health` is OK
  - an **upgrade** from the previous version's installer: a backup is taken and migrations run
  - an **upgrade with a broken migration** (a test build) rolls back to the previous version and the data
  - an uninstall keeps the data folder
- **Backups:**
  - backup → verify → restore round trip on SQL Server and on PostgreSQL, with the data files and data-protection keys, after which an existing PIN still logs in
  - an encrypted backup with the right and the wrong password
  - the retention rule
  - the failed-backup banner
  - the 48-hour banner
- **Updates:**
  - the feed and its signature check
  - a terminal never installs while a user is logged in
  - the server-first rule
  - an old terminal against a new server gets the forced update
  - the API contract test between the previous and current versions
- **Release workflow:** a dry run on a test tag in a fork, or with `act`, produces every artifact, the checksums and `updates.json`.
- **Security:**
  - login rate limits
  - security headers present
  - no hash or token fields in any DTO
  - the dependency audits run

## Acceptance checklist

- [ ] On a clean Windows 11 VM, run `TausiPOS-Server-Setup.exe`, choosing **Install SQL Server Express**. The service runs after a reboot, the firewall rule exists, and the Finish page shows the address and the setup token. The wizard (Task 14) completes in the browser.
- [ ] On a second VM, `TausiPOS-Setup.exe` installs the terminal. It starts full-screen at login, pairs with the server, and can't be closed with Alt+F4.
- [ ] **Back up now** creates a verified `.tausibak`, with a second copy in a chosen folder. Restoring it on a **third** VM (new install → Restore from a backup) brings back the business, and an existing PIN still logs in.
- [ ] Install the previous release, then upgrade to this one. A backup is taken first, and the data is kept. A deliberately broken build rolls back by itself.
- [ ] Publish a newer terminal version through the server. The terminal updates only while it's idle on the PIN screen, never mid-order.
- [ ] An unplugged network share as the second-copy folder gives the "backup copy failed" banner, but the local backup still succeeds.
- [ ] The Linux tarball installs on Ubuntu 24.04 with `--with-postgres`, and the service runs.
- [ ] `docs/INSTALL.md` and `docs/TROUBLESHOOTING.md` are written so that I can follow them without Codex.
- [ ] All tests, the dependency audits and CI pass. Push `release/15-installer` and stop.

When done, report as `AGENTS.md` describes, with what needs buying or setting up outside the code (the code-signing certificate, the updater signing key, the update feed hosting), and anything you decided differently from this prompt.
