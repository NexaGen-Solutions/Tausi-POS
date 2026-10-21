# Running Tausi POS

Everything except the Windows terminal installer builds and runs on Ubuntu.

## 1. What you need

| Tool | Version | Used for |
| --- | --- | --- |
| .NET SDK | 10.0 | Tausi Server and its tests |
| Node.js | 24 (with npm) | Web UI, Tauri CLI |
| Docker | any recent | Development databases and the integration tests |
| Rust | stable (via rustup) | Only for the terminal app |
| Tauri system libraries | see below | Only for the terminal app |

Tauri on Ubuntu needs:

```bash
sudo apt install libwebkit2gtk-4.1-dev build-essential curl wget file libxdo-dev \
  libssl-dev libayatana-appindicator3-dev librsvg2-dev
```

## 2. From a clean clone

```bash
git clone <repo> tausi-pos && cd tausi-pos

# Database (PostgreSQL 16 on localhost:5432, database tausipos, user tausi / tausi_dev)
docker compose -f docker-compose.dev.yml up -d

# Web UI: install and build once (the server serves web/dist in development)
cd web && npm ci && npm run build && cd ..

# Server: tools, demo data, then run
cd server
dotnet tool restore
export TAUSI_ADMIN_PASSWORD='choose-a-dev-password'
dotnet run --project src/Tausi.Api -- seed --demo
dotnet run --project src/Tausi.Api
```

The seed prints each user's 4-digit PIN **once**. Write them down. Running it again changes
nothing and prints no PINs. To start over, drop the database
(`docker compose -f docker-compose.dev.yml down -v`) and seed again.

| User | Role | Signs in with |
| --- | --- | --- |
| Administrator | Admin | PIN, and the admin site as `admin` with `TAUSI_ADMIN_PASSWORD` |
| MAU | Supervisor | PIN |
| CAROL | Cashier | PIN |
| edna, CYNTHIA, Ruth, WAMBOO | Waiter | PIN |

The server listens on <http://localhost:5080>. It applies database migrations and the base seed
(roles, permissions, ledger accounts) by itself on start.

### Web UI with hot reload

```bash
cd web && npm run dev     # http://localhost:5173, proxies /api, /hubs and /files to :5080
```

## 3. Try it

### Pair the browser as a terminal

1. Open <http://localhost:5173/pos> (or `:5080/pos`). A browser that is not paired shows a
   6-digit **pairing code**.
2. Sign in to <http://localhost:5173/admin> as `admin` and open **Terminals and printers**.
   Press **Pair a terminal**, type the code, choose the terminal (for example **Counter A**, or
   **A new terminal** with a name) and press **Pair**. Codes last 10 minutes and work once; five
   wrong codes in 5 minutes block that admin from pairing for 5 minutes. Pairing a terminal that
   is already paired asks first and logs the old device out.
3. Within two seconds the browser shows "Paired as Counter A" and moves on to the PIN pad. Log in with a PIN
   from the seed (tap the keypad or type it; it submits by itself at 4 digits).
4. After login the terminal shows the **table map**. If no day is open it says "The day isn't
   open yet": MAU (Supervisor) or Administrator also get **Open day** there; waiters can look but
   not start tickets. Other terminals' PIN screens show "Work period open since …" straight away.

The device token is kept in the browser's `localStorage` (`tausi.device`). Clear it, or press
**Edit → Revoke** for the terminal on the admin site, to pair again: a revoked terminal that is
running goes back to its pairing screen within two seconds.

For the curl examples below, sign in to the admin API once:

```bash
curl -c /tmp/nx.cookies -H 'content-type: application/json' \
  -d "{\"userName\":\"admin\",\"password\":\"$TAUSI_ADMIN_PASSWORD\"}" \
  http://localhost:5080/api/admin/login
```

### Staff and PINs (admin site)

Sign in to <http://localhost:5173/admin> as `admin` and open **Staff and PINs**:

- **Add staff** with a starting PIN. The PIN is shown once; the person must choose their own at
  first login (entered twice; 1234-style PINs and other people's PINs are refused).
- **Reset PIN** (type one or press Generate): the person is logged out on every terminal at once.
- **Deactivate / Reactivate**: nobody is ever deleted.
- To sign in as a supervisor, edit MAU and set an admin-site password, then sign in as `mau`.
  Supervisors see and manage only waiters and cashiers.

From the command line (development), a user can also be made to choose a new PIN at their next
login:

```bash
dotnet run --project src/Tausi.Api -- require-pin-change edna
```

### Idle logout

Terminals log out after 10 minutes without use. To try it quickly, set it to 1 minute (admin
cookie from the sign-in above):

```bash
curl -s -b /tmp/nx.cookies http://localhost:5080/api/settings/business > /tmp/settings.json
# edit otherJson in /tmp/settings.json to {"pos":{"idleLogoutMinutes":1,"logoutAfterSettle":false}}
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' --data @/tmp/settings.json \
  http://localhost:5080/api/settings/business
```

### Menu and prices

`seed --demo` adds the demo menu when the menu is empty; `seed --demo-menu` adds only the menu.
Open **Menu and prices** on the admin site to edit items, prices (with the price log), option
groups and price lists, and to import or export CSV. The ticket screen on a terminal (tap a table
twice) shows a temporary "Menu (for testing)" list; it refreshes when the menu changes.

For now these are API only (admin cookie from the sign-in above):

```bash
# A terminal sells from another price list (null: back to the default)
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' \
  -d '{"priceListId":"<price list id>"}' http://localhost:5080/api/admin/terminals/<terminal id>/price-list
# Change one cell of the permission grid, e.g. give Supervisors menu.edit
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' \
  -d '{"permissionKey":"menu.edit","value":"Allowed"}' http://localhost:5080/api/admin/roles/<role id>/permissions
```

Role ids are in `GET /api/admin/staff` (`roleId`, `assignableRoles`).

### Table map and tickets

`seed --demo` adds Mau's layout (tables 1–16 in Main, Pool 1–2, Bar 1–2). A day can't close while
tickets are open, so demo tickets only go into an open day:

1. On a fresh database the map shows "The day isn't open yet". Log in as MAU and press **Open day**.
2. Run `seed --demo` again: it adds a few open tickets to that day (two on Table 7, one idle on
   Table 5 with no activity for 48 minutes, and others). The map shows them at once.

- **Tap** a table to see its tickets on the right; **tap twice** to open it (a free table opens a
  new draft, a table with one ticket opens that ticket) in the order screen (below).
- **Locks:** open the same ticket on two browser terminals. The second shows "Open on Counter A by
  edna. You can view it but not change it." Press Tables on the first; the second becomes
  editable within two seconds. A lock not renewed for 90 seconds, or whose user logs out or is
  logged out for being idle, is released within 5 seconds.
- **Merge** on Table 7: Merge tickets, tick both, confirm "Merge #… into #…?". The oldest is kept and
  every line keeps its waiter. **Move**: Move to another table, then tap the new table.
- To try approvals, set waiters' `ticket.merge` to NeedsApproval (curl in the Menu section above);
  merging then asks for a supervisor PIN.
- **Idle tables** turn orange after `pos.tableIdleMinutes` (default 30) without activity. To see it
  quickly, set it to 1 in the settings JSON (as for idle logout above, with
  `{"pos":{"idleLogoutMinutes":10,"logoutAfterSettle":false,"tableIdleMinutes":1}}`).
- **Offline:** stop the server; the map shows "Not connected to the server. Reconnecting…" and
  every button is disabled until it is back.
- **Admin site → Tables** lists areas and tables, with **Quick add** ("from 1 to 16 in Main", with
  an optional name before the number such as Pool). A table with open tickets can't be switched off.

To get back to "no open day" in development (closing days properly comes with the cash-up). Like
the cash-up will, it refuses while tickets are open and lists them:

```bash
dotnet run --project src/Tausi.Api -- close-day
```

### Order screen

Log in as a waiter (e.g. edna) and tap a free table twice. A new ticket is saved with its first
item and gets its number then.

- Tap an item to add 1. Beers open the **Cold / Warm** pop-up with Cold chosen; set the quantity
  and press **Add 2 × Tusker (Cold) · KSh 500**. Search finds items in every category by name or
  short name.
- Tap a line to select it: a New line has −, + and Remove; a Printed line has Void and Gift
  (disabled until voids and approvals come).
- **Print (n new)** sends the customer's bill with every line first, then the dispense slip with
  only the new lines. With nothing new it reads **Print** and reprints the bill, as **Reprint bill**
  does (waiters need a supervisor PIN for it in the demo). Each print is a new bill number; from
  bill 2 the bill says "Replaces earlier bills".
- In development the demo printers write to files: `server/src/Tausi.Api/print-output/` gets
  `…-Ticket-Printer-1-Bill-….txt` and `…-Ticket-Printer-1-Dispense-….txt` (Counter A prints both
  on Ticket Printer 1). Each file starts with a header line naming the printer and job.
- **Tables** with unprinted lines asks "Print now / Leave them unprinted / Stay"; a table left with
  unprinted lines shows "2 not printed" on its tile. **Move table** goes to the map to tap the new
  table.
- **Settle** is for cashiers (`ticket.settle`); it opens a placeholder until the settle screen.
- To see a failed print: edit Ticket Printer 1 to a Network printer at `127.0.0.1:9`, press Print,
  and after about 15 s the order screen shows "Bill not printed on Ticket Printer 1 · Retry". Set
  it back to File and press Retry.

**Send and bill mode** (two buttons: **Send (n new)** prints only the dispense slip, **Print
bill** prints the bill): set `printMode` in the business settings, as for idle logout above:

```bash
curl -s -b /tmp/nx.cookies http://localhost:5080/api/settings/business > /tmp/settings.json
# change "printMode": "OneButton" to "printMode": "SendAndBill"
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' --data @/tmp/settings.json \
  http://localhost:5080/api/settings/business
```

Reload the terminal to pick it up.

**Time zone:** bills and slips print the business's time (`timeZone`, an IANA name; the demo is
`Africa/Nairobi`), whatever zone the server runs in. Change it the same way: set `"timeZone"` in
`/tmp/settings.json` (for example `"Africa/Kampala"`) and PUT it back. Unknown names are refused.

**VAT on bills:** an item marked **VAT exempt** (admin site → Menu → item → VAT rate) is listed as
"VAT exempt: KSh …"; a **Zero-rated (0%)** item is in the VAT lines as "Total includes VAT 0%:
KSh 0.00".

### Admin site

<http://localhost:5173/admin>: sign in as `admin`. All sidebar items are present; the pages
come in later tasks.

### Lockout, approvals and idempotency (with curl)

```bash
D=<device token from localStorage 'tausi.device'>
# 5 wrong PINs within 2 minutes: the 5th answers 429 and the terminal is locked for 60 s.
curl -H "X-Device-Token: $D" -H 'content-type: application/json' -d '{"pin":"0000"}' http://localhost:5080/api/auth/pin

# Approval flow (temporary dev-only endpoint guarded by order.void):
T=$(curl -s -H "X-Device-Token: $D" -H 'content-type: application/json' -d '{"pin":"<edna PIN>"}' \
  http://localhost:5080/api/auth/pin | jq -r .token)
curl -X POST -H "X-Device-Token: $D" -H "Authorization: Bearer $T" http://localhost:5080/api/dev/void-probe
#   -> 403 with "approvalRequired": true
A=$(curl -s -H "X-Device-Token: $D" -H "Authorization: Bearer $T" -H 'content-type: application/json' \
  -d '{"pin":"<MAU PIN>","permissionKey":"order.void"}' http://localhost:5080/api/auth/approve | jq -r .approvalToken)
curl -X POST -H "X-Device-Token: $D" -H "Authorization: Bearer $T" -H "X-Approval-Token: $A" \
  http://localhost:5080/api/dev/void-probe     # 200 once; the same token again -> 403
```

Any write with an `Idempotency-Key` header returns the original answer when repeated
(header `Idempotency-Replayed: true`), and 422 if the same key comes with a different body.

### Terminals and printers (admin site)

The demo has four printers, one per terminal (Ticket Printer 1, Pillar 1, Ticket Printer 2,
Pillar 2). Each terminal dispenses on its own printer; receipts print at the counter (Counter A
and Pillar 1 on Ticket Printer 1, Counter B and Pillar 2 on Ticket Printer 2). Running
`seed --demo` again converts a database seeded with an earlier printer setup. In development the
demo printers use the **File** connection, so every slip is written as text to
`server/src/Tausi.Api/print-output/`.

Open **Terminals and printers** on the admin site:

- The terminals table shows each terminal live: Online, Offline ("last seen 3 min ago", 30 s after
  it disconnects) or Revoked, with who is logged in. **Edit** sets the name, price list, dispense
  printer and header, receipt printer and active, with a routing preview.
- **Add printer** / **Edit**: Network (IP and port), Windows share (`\\PC\Share`), Via terminal
  (the terminal it is plugged into; the Windows printer name comes from a list the terminal app
  reports) or File. **Check connection** says "Reachable" or what is wrong. Network printers are
  checked every 60 s.
- **Test print** prints a column ruler, text styles and a sample bill and dispense slip.
- **Failed jobs (n)**: Retry, Send to another printer, Discard (with a reason) and Retry all.

To see a failure: add a Network printer at `127.0.0.1:9` (nothing listens there), press Check
connection, then Test print. After about 15 s the job is in Failed jobs; send it to Ticket
Printer 1 and it appears in `print-output/`.

`Tausi__Printing__Transport=File` sends every printer's jobs to files, whatever its connection.

### Terminal app (Tauri)

```bash
cd terminal && npm ci
npm run tauri dev
```

This starts the Vite dev server and opens the terminal window at 1280×800 (set
`TAUSI_FULLSCREEN=1` for full screen without a title bar, as on the counters). Pair it as above.
The app only talks to the server it was paired with. To change the server address, press
**Change server address** on its setup screen and sign in with an admin-site login first.
In development the window always loads the Vite dev server; the device token is kept in the
app's store (`tausi.json` in the app data folder).

### Upgrading from the Nexagen POS names

The product was renamed from Nexagen POS to Tausi POS. Installs set up with the old names keep
working:

- **Settings and environment variables.** `NEXAGEN_*` variables (e.g. `NEXAGEN_ADMIN_PASSWORD`),
  the `Nexagen` settings section (`Nexagen__DataDirectory`) and the `ConnectionStrings__Nexagen`
  connection string are still read when the new name (`TAUSI_*`, `Tausi__…`,
  `ConnectionStrings__Tausi`) is not set. The server logs a warning once for each old name it uses;
  rename them when convenient.
- **Existing dev databases named NexagenPOS:** either rename the database or point the
  connection string at the old name. The dev database is now `tausipos` (user `tausi`), in new
  Docker volumes (`tausi-pgdata`, `tausi-mssqldata`), so `docker compose up` starts an empty one
  and the old volumes are left as they were. To keep working on the old data instead:

  ```bash
  export ConnectionStrings__Tausi='Host=localhost;Port=5432;Username=nexagen;Password=nexagen_dev;Database=nexagen'
  ```

  (with the old `pgdata` volume running), or recreate the new one with `seed --demo`.
- **Dev data folder.** Server secrets (the PIN pepper and signing keys) live in
  `server/src/Tausi.Api/data/`. A checkout from before the rename has them in
  `server/src/Nexagen.Api/data/`, which git leaves behind; on start the server copies that folder
  once if the new one is missing, so existing PINs keep working. The old folder can then be deleted.
- **Windows service.** The service is now `TausiPOSServer`. Replace an old "Nexagen Server"
  service: `sc.exe stop "Nexagen Server"`, `sc.exe delete "Nexagen Server"`, then create the new
  one as above.
- **Terminal app.** The app identifier changed (`com.nexagen.terminal` →
  `com.nexagensolutions.tausipos`). On first start the new app copies the old app's settings, so
  the terminal stays paired. Uninstall the old "Nexagen Terminal" app afterwards so only one
  starts with Windows.

### Testing on real hardware

1. **Build the installer.** Every CI run of a branch builds it: open the run on GitHub and
   download the **tausi-terminal-windows** artifact (kept 14 days), which holds
   `TausiPOS-Setup.exe`. A `terminal-v*` tag runs the release workflow, which attaches it to a
   release. Or build it on a Windows PC with Rust, Node
   and the WebView2 runtime: `cd web && npm ci && npm run build`, then
   `cd ../terminal && npm ci && npm run tauri build`. The installer is in
   `terminal/src-tauri/target/release/bundle/nsis/`.
2. **Install and pair.** Install it on a Windows 10/11 counter PC on the same network as the dev
   server. At first start enter the server address (e.g. `http://<dev PC IP>:5080`; open port
   5080 in the dev PC's firewall and run the server with `--urls http://0.0.0.0:5080`). The app
   shows a code; pair it from `/admin/terminals`.
3. **Set up the printer.**
   - **USB:** install the printer's Windows driver, then **Add printer → Via terminal (USB)**,
     choose the counter PC and pick the Windows printer name from the list. Check connection.
   - **Network:** **Add printer → Network**, type its IP (port 9100), then **Check connection**.

   Then set it as the terminal's receipt or dispense printer in **Edit**.
4. **Test print.** The column ruler must fit exactly on one line (42 characters on 80 mm, 32 on
   58 mm). If it wraps or stops short, the paper width is wrong. Check the cut, the bold and
   double-height lines, and the logo if one is set.
5. **Failure and retry.** Unplug the printer (or switch it off) and test print: the job shows in
   **Failed jobs**. Plug it back in, press **Retry**, and it prints.

## 4. Tests

```bash
cd server && dotnet test        # unit + integration
cd web && npm test && npm run lint
```

The integration tests start PostgreSQL 16 with Testcontainers, so Docker must be running. To use
an existing server instead, set `TAUSI_TEST_POSTGRES` (each run creates and drops its own
database):

```bash
TAUSI_TEST_POSTGRES='Host=localhost;Port=5432;Username=tausi;Password=tausi_dev' dotnet test
```

The SQL Server migration test starts SQL Server 2022 with Testcontainers, or uses
`TAUSI_TEST_SQLSERVER`; without Docker or that variable it is skipped.

## 5. SQL Server

```bash
docker compose -f docker-compose.dev.yml --profile sqlserver up -d
export Database__Provider=SqlServer
export ConnectionStrings__Tausi='Server=localhost,1433;Database=tausipos;User Id=sa;Password=Tausi_dev_123;TrustServerCertificate=True'
dotnet run --project src/Tausi.Api -- seed --demo
```

## 6. Changing the database model

Each provider has its own migrations. Add both:

```bash
cd server
dotnet ef migrations add <Name> --project src/Tausi.Infrastructure --startup-project src/Tausi.Infrastructure \
  --context PostgresTausiDbContext --output-dir Migrations/Postgres
dotnet ef migrations add <Name> --project src/Tausi.Infrastructure --startup-project src/Tausi.Infrastructure \
  --context SqlServerTausiDbContext --output-dir Migrations/SqlServer
```

## 7. Production

### Build

```bash
cd web && npm ci && npm run build && cd ../server
dotnet publish src/Tausi.Api -c Release -r linux-x64 --self-contained false -o /opt/tausi   # Linux
dotnet publish src/Tausi.Api -c Release -r win-x64   --self-contained false -o out\tausi    # Windows
```

Publishing copies `web/dist` into `wwwroot`, so the server alone serves the terminal screens and
the admin site.

Settings come from `appsettings.json` next to the program, or environment variables:

| Setting | Example |
| --- | --- |
| `ConnectionStrings__Tausi` | `Host=localhost;Database=tausipos;Username=tausi;Password=...` |
| `Database__Provider` | `Postgres` or `SqlServer` |
| `Tausi__DataDirectory` | `/var/lib/tausi` (uploads and server secrets; **back it up with the database**) |
| `Urls` | `http://0.0.0.0:5080` |

Open port 5080 in the firewall and give the server a fixed IP.

The data directory holds `secrets/pin-pepper.key`. Without it no PIN works and every PIN must be
reset, so it is part of the backup.

### Linux (systemd)

`/etc/systemd/system/tausi.service`:

```ini
[Unit]
Description=Tausi POS Server by NexaGen Solutions
After=network-online.target postgresql.service

[Service]
Type=notify
WorkingDirectory=/opt/tausi
ExecStart=/usr/bin/dotnet /opt/tausi/Tausi.Api.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Tausi__DataDirectory=/var/lib/tausi
EnvironmentFile=-/etc/tausi/env
User=tausi
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
sudo useradd --system tausi && sudo mkdir -p /var/lib/tausi && sudo chown tausi /var/lib/tausi
sudo systemctl daemon-reload && sudo systemctl enable --now tausi
```

### Windows (service)

```powershell
sc.exe create TausiPOSServer binPath= "C:\Tausi\Tausi.Api.exe" start= auto DisplayName= "Tausi POS Server"
sc.exe description TausiPOSServer "Tausi POS server by NexaGen Solutions"
setx /M ConnectionStrings__Tausi "Server=.\SQLEXPRESS;Database=TausiPOS;Trusted_Connection=True;TrustServerCertificate=True"
setx /M Database__Provider SqlServer
setx /M Tausi__DataDirectory "C:\ProgramData\Tausi"
sc.exe start TausiPOSServer
```

The service account needs rights on the SQL Server database (for example add
`NT SERVICE\TausiPOSServer` as a login with `db_owner` on `TausiPOS`).

### Terminal installer

Push a tag `terminal-v<version>` (e.g. `terminal-v0.1.0`). GitHub Actions builds the Windows
installer (NSIS) on `windows-latest` and attaches it to the release as `TausiPOS-Setup.exe`.
It installs as "Tausi POS" (publisher NexaGen Solutions). On first start the app asks
for the server address (e.g. `http://POSSERVER2:5080`), shows a pairing code, and after pairing
opens full screen on the PIN pad. It starts with Windows.
