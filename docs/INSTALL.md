# Installing Tausi POS

For the bar owner and the NexaGen technician. Tausi POS has two parts:

- **Tausi POS Server**: one PC in the back office holds all the data, the rules and the printing.
- **Tausi POS terminals**: the counter PCs with touch screens, which show the POS screens from the server.

Everything runs on the bar's own network. The server needs no internet to trade.

> **Run the server on the bar's own network. Don't expose port 5080 to the internet.**

---

## 1. What you need

| | Server PC | Each counter PC |
| --- | --- | --- |
| Windows | Windows 10 or 11 (64-bit), or Windows Server | Windows 10 or 11 (64-bit) |
| Memory | 4 GB or more | 4 GB or more |
| Disk | An SSD, at least 20 GB free | Any |
| Network | **Wired** (a cable, not Wi-Fi) | Wired if possible |
| Power | **A UPS** (battery backup), so a power cut doesn't stop the bar or damage the data | |

The server can also run on Linux (Ubuntu 22.04 or 24.04): see [Linux server](#9-linux-server).

**Give the server PC a fixed IP address**, so the counter PCs always find it. The easiest way is a
"DHCP reservation" in the router for the server PC (ask whoever manages the router), or set it in
Windows: Settings → Network → Ethernet → IP assignment → Edit → Manual.

## 2. Install the server

1. Copy `TausiPOS-Server-Setup-<version>.exe` to the server PC and double-click it. Windows asks for
   permission to make changes: choose **Yes**.
2. **Welcome**: read and accept the licence agreement.
3. **Database**: where Tausi POS keeps its data.
   - **Install SQL Server Express (recommended)**: the installer downloads Microsoft SQL Server 2022
     Express and sets it up for Tausi POS (instance `TAUSI`). No internet on the server? You'll be asked
     for the SQL Server Express installer file (`SQLEXPR_x64_ENU.exe`) from a USB drive.
   - **Use an existing SQL Server**: for PCs that already have one (for example the SQL Server Express
     that SambaPOS installed on POSSERVER2). Enter the server or instance (`.\SQLEXPRESS`), choose Windows
     sign-in or a SQL login, and press **Test connection**. Tausi POS creates its **own** database,
     `TausiPOS`, and never touches the others (SambaPOS's stays as it is).
   - **Use PostgreSQL**: for technicians who prefer it. Host, port, user and password.
   - **Restore from a backup**: when moving to a new PC (see [Moving to a new PC](#6-moving-to-a-new-pc)).
4. **Port**: 5080 suits nearly every bar. Keep **Open the Windows Firewall for the local network** ticked.
5. **Installing**: a few minutes (longer when SQL Server Express is downloaded).
6. **Finish**: the page shows
   - the **server address** to type on each counter PC, for example `http://192.168.1.10:5080`;
   - the **setup token** (eight letters and numbers, like `K7QM-3HXP`);
   - **Open setup in the browser**: the setup wizard, where you enter the business, the owner, staff,
     menu, opening stock and the licence key.

What the installer sets up:

| What | Where |
| --- | --- |
| The program | `C:\Program Files\Tausi POS\Server\` |
| Settings, data, logs, backups | `C:\ProgramData\Tausi POS\` (`config`, `data`, `logs`, `backups`) |
| The service | **Tausi POS Server** (`TausiPOSServer`), starts automatically, restarts by itself after a crash |
| The firewall rule | **Tausi POS Server**: the port, private (home or work) networks only |

The database password (if any) is stored encrypted for this PC. Only Windows administrators and the
service can open the settings, data and backups folders.

### Silent install (for technicians)

Every screen's answer can be given on the command line:

```bat
TausiPOS-Server-Setup.exe /quiet DB=existing SQLINSTANCE=.\SQLEXPRESS PORT=5080
```

| Option | Meaning |
| --- | --- |
| `DB=sqlexpress` (default), `existing`, `postgres` | Which database |
| `SQLINSTANCE=.\SQLEXPRESS` | The SQL Server instance (`DB=existing`) |
| `SQLGRANTSYSTEM=1` | With Windows sign-in to an existing SQL Server: let the installer set up the database (see below) |
| `SQLUSER=…` `SQLPASSWORD=…` | A SQL login instead of Windows sign-in |
| `SQLMEDIA=D:\SQLEXPR_x64_ENU.exe` | SQL Server Express from a USB drive (`DB=sqlexpress`, no internet) |
| `PGHOST=` `PGPORT=` `PGUSER=` `PGPASSWORD=` `PGDATABASE=` | PostgreSQL (`DB=postgres`) |
| `PORT=5080` | The port |
| `FIREWALL=0` | Don't add the firewall rule |
| `RESTOREFILE=D:\backup.tausibak` `RESTOREPASSWORD=…` | Restore a backup (a new PC) |
| `/log C:\temp\install.log` | Write the installer's log there |

`/passive` shows only a progress bar. The exit code is 0 when it worked.

With **Windows sign-in to an existing SQL Server**, the installer's database steps run as the PC's SYSTEM
account, which SQL Server 2012 and later don't make an administrator. The Database page's **Let the
installer set up the database** (ticked) adds `NT AUTHORITY\SYSTEM` as a SQL Server administrator, signed
in as you, so you must be one (the Windows user who installed SQL Server usually is). Silently, add
`SQLGRANTSYSTEM=1`. Prefer not to? Install with a SQL login (`SQLUSER`, `SQLPASSWORD`) that may create
databases.

## 3. Install the terminals

1. Copy `TausiPOS-Setup-<version>.exe` to the counter PC and run it (choose **Yes** when Windows asks).
   It installs to `C:\Program Files\Tausi POS\Terminal\` for every user, with Microsoft Edge WebView2 if
   it is missing (use `TausiPOS-Setup-<version>-offline.exe` on a PC without internet).
2. The app starts full screen. Type the server address (from the server's Finish page) and press
   **Connect**. It shows a 6-digit **pairing code**.
3. On the admin site (`http://<server>:5080/admin`), open **Terminals and printers → Pair a terminal**,
   type the code and choose the terminal. Within seconds the counter PC shows the PIN pad.

The terminal starts by itself when Windows starts, full screen, and can't be closed with Alt+F4
(**kiosk mode**). The pairing is kept encrypted for that Windows user.

**Silent install**: `TausiPOS-Setup.exe /S SERVER=http://192.168.1.10:5080` installs without questions
and fills in the server address on the first screen.

**Leaving kiosk mode** (technicians): tap the **top-left corner of the screen five times** quickly, then
sign in with an admin-site user name and password. Tick **Also switch kiosk mode off on this PC** to
keep it off (the app then opens in a normal window). To turn kiosk mode on again, delete
`%APPDATA%\com.nexagensolutions.tausipos\tausi.json`'s `"kiosk": false` line, or set the environment
variable `TAUSI_KIOSK=0` only for testing.

## 4. Printers

Add printers on the admin site under **Terminals and printers → Add printer**:

- **Network printer** (most kitchen and bar printers): its IP address, port 9100.
- **Windows shared printer**: the share name (`\\COUNTER1\Bar`).
- **Plugged into a terminal** (USB): choose that terminal and the Windows printer name.

Press **Test print** and **Ruler test**: the ruler shows whether the paper width (58 or 80 mm) is right.

## 5. Backups

Tausi POS backs up by itself **every night at 05:00** (business time), and before every update and
restore. Each backup is one `.tausibak` file in `C:\ProgramData\Tausi POS\backups\`, with the database,
the keys and the uploaded files. Every backup is checked after it is made.

**Set up a second copy** (Settings → Backups → *Second copy to*): a USB drive (`E:\Backups`), a network
share (`\\NAS\Tausi`) or a synced folder (OneDrive, Google Drive). A PC can break; the second copy is what
saves the business. If the copy fails (the drive is unplugged), the banner says so, and the backup on the
server is still made.

**Encryption** (optional): tick *Encrypt backups with a password*. **Write the password down and keep it
safe**: without it an encrypted backup can't be restored by anyone, including NexaGen.

**Kept**: the last 14 nightly backups, the first backup of each month for 12 months, and other backups
for 30 days.

A red banner shows on the admin site and the Close day screen when last night's backup failed, or when
no backup has worked for 48 hours.

**Back up now** and **Download** are on Settings → Backups. **Restore** there replaces everything with
the backup (a backup of how things are now is taken first). If the admin site won't start, restore from
the server PC (as administrator):

```bat
net stop TausiPOSServer
"C:\Program Files\Tausi POS\Server\TausiServer.exe" restore D:\tausi-20261025-050000-nightly.tausibak
net start TausiPOSServer
```

## 6. Moving to a new PC

1. On the old server: Settings → Backups → **Back up now**, then **Download** it (or take the newest
   from the second-copy folder).
2. On the new PC: run the server installer. On the **Database** page tick **Restore from a backup**
   and choose the file (and its password if it is encrypted). Setup is skipped: the business, staff,
   menu and PINs come from the backup.
3. Point the terminals at the new address, in one action: on the **old** server's admin site, open
   **Terminals and printers → Move terminals to a new server** and type the new address. Every running
   terminal goes there and stays paired (the restored backup knows them). Then switch the old server off.
   A terminal that was off: choose **Change server** on its screen and type the new address (it pairs
   again).
4. Or give the new PC the old one's fixed IP address: the terminals find it with no change at all.

## 7. Updates

**The server**: Settings → **System** → **Check for updates** (needs internet on the server). **Install**
works when the day is closed; with the day open you must type `UPDATE` to confirm. The update backs up
first, installs, and starts again; if anything fails it goes back to the previous version and its data
by itself. No internet? NexaGen can give you a `.tausiupdate` file: Settings → System → *Update from a
USB drive*. Updates are checked against NexaGen's signature before they run.

**The terminals** update **from the server**, never from the internet: after the server is updated, each
terminal downloads the matching version and installs it when it has sat on the PIN screen for 5 minutes
with nobody logged in (never in the middle of an order), or at its next start. A terminal too old for the
server shows "Updating, please wait" and updates before anyone logs in. Windows may ask for permission
to install (choose **Yes**) if the counter PC's Windows user isn't an administrator.

## 8. Uninstall

Settings → Apps → **Tausi POS Server** → Uninstall. It removes the service, the program and the firewall
rule, and **keeps** `C:\ProgramData\Tausi POS\` and the database, so installing again picks up where it
left off. Tick **Also delete all data** and type the business name to delete everything (this can't be
undone: download a backup first).

Silent: `TausiPOS-Server-Setup.exe /uninstall /quiet` (keeps the data;
add `DELETEDATA=1 BUSINESSNAME="Mau's Village"` to delete it).

The terminal: Settings → Apps → **Tausi POS** → Uninstall, or `"C:\Program Files\Tausi POS\Terminal\uninstall.exe" /S`.

## 9. Linux server

On Ubuntu 22.04 or 24.04:

```bash
tar xzf tausi-server-<version>-linux-x64.tar.gz
cd tausi-server-<version>-linux-x64
sudo ./install.sh --with-postgres
```

It creates the `tausi` user and `/opt/tausi` (program), `/var/lib/tausi` (data and backups),
`/var/log/tausi` (logs) and `/etc/tausi/tausi.env` (settings, readable by root and tausi only); installs
PostgreSQL with a random password (`--with-postgres`); opens the port in `ufw` for the local network
only; starts the `tausi` service; and prints the address and the setup token.

To upgrade, unpack the new version and run `sudo ./install.sh` again: it backs up first and goes back to
the previous version if the new one doesn't start. Backups use the same Settings → Backups page
(with `pg_dump`).

**Run the server on the bar's own network. Don't expose port 5080 to the internet.**

## 10. HTTPS for the admin site (optional)

The terminals talk to the server over plain HTTP on the bar's network. An owner who wants the admin site
encrypted can turn on HTTPS with a certificate made on the server (as administrator):

```bat
"C:\Program Files\Tausi POS\Server\TausiServer.exe" enable-https --port 5443
net stop TausiPOSServer & net start TausiPOSServer
```

It prints the certificate's fingerprint. Browsers warn about a certificate they don't know: check the
fingerprint matches, then continue. Open the firewall for 5443 too. `disable-https` turns it off.

## 11. Commands on the server

Run as administrator in `C:\Program Files\Tausi POS\Server\` (Linux: `sudo -u tausi /opt/tausi/TausiServer`
with the settings from `/etc/tausi/tausi.env`).

| Command | What it does |
| --- | --- |
| `TausiServer backup` | A backup now |
| `TausiServer restore <file> [--password P]` | Restores a backup (stop the service first) |
| `TausiServer reset-admin` | A lost admin-site password: prints a token to use at `/setup/recover` |
| `TausiServer test-db` | Checks the database connection |
| `TausiServer wait-health` | Waits until the server answers |
| `TausiServer enable-https` / `disable-https` | The optional HTTPS for the admin site |
